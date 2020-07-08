﻿using Microsoft.CodeAnalysis;
using StrongInject.Runtime;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace StrongInject.Generator
{
    internal class RegistrationCalculator
    {
        public RegistrationCalculator(
            Compilation compilation,
            Action<Diagnostic> reportDiagnostic,
            CancellationToken cancellationToken)
        {
            _reportDiagnostic = reportDiagnostic;
            _cancellationToken = cancellationToken;
            _registrationAttributeType = compilation.GetType(typeof(RegistrationAttribute))!;
            _moduleRegistrationAttributeType = compilation.GetType(typeof(ModuleRegistrationAttribute))!;
            _iFactoryType = compilation.GetType(typeof(IFactory<>))!;
            _iRequiresInitializationType = compilation.GetType(typeof(IRequiresInitialization))!;
            if (_registrationAttributeType is null || _moduleRegistrationAttributeType is null || _iFactoryType is null || _iRequiresInitializationType is null)
            {
                _valid = false;
                //ToDo Report Diagnostic
            }
            else
            {
                _valid = true;
            }
        }

        private Dictionary<INamedTypeSymbol, Dictionary<ITypeSymbol, Registration>> _registrations = new();
        private INamedTypeSymbol _registrationAttributeType;
        private INamedTypeSymbol _moduleRegistrationAttributeType;
        private INamedTypeSymbol _iFactoryType;
        private INamedTypeSymbol _iRequiresInitializationType;
        private readonly Action<Diagnostic> _reportDiagnostic;
        private readonly CancellationToken _cancellationToken;
        private bool _valid;

        public IReadOnlyDictionary<ITypeSymbol, Registration> GetRegistrations(INamedTypeSymbol module)
        {
            if (!_valid)
            {
                return ImmutableDictionary<ITypeSymbol, Registration>.Empty;
            }

            if (!_registrations.TryGetValue(module, out var registrations))
            {
                registrations = CalculateRegistrations(module);
                _registrations[module] = registrations;
            }
            return registrations;
        }

        private Dictionary<ITypeSymbol, Registration> CalculateRegistrations(
            INamedTypeSymbol container)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            var attributes = container.GetAttributes();

            var directRegistrations = CalculateDirectRegistrations(attributes);

            var moduleRegistrations = new List<(AttributeData, Dictionary<ITypeSymbol, Registration> registrations)>();
            foreach (var moduleRegistrationAttribute in attributes.Where(x => x.AttributeClass?.Equals(_moduleRegistrationAttributeType, SymbolEqualityComparer.Default) ?? false))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var moduleConstant = moduleRegistrationAttribute.ConstructorArguments.FirstOrDefault();
                if (moduleConstant.Kind != TypedConstantKind.Type)
                {
                    // Invalid code, ignore;
                    continue;
                }
                var moduleType = (INamedTypeSymbol)moduleConstant.Value!;

                var exclusionListConstants = moduleRegistrationAttribute.ConstructorArguments.FirstOrDefault(x => x.Kind == TypedConstantKind.Array).Values;
                var exclusionList = exclusionListConstants.IsDefault
                    ? new HashSet<INamedTypeSymbol>()
                    : exclusionListConstants.Select(x => x.Value).OfType<INamedTypeSymbol>().ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

                var registrations = GetRegistrations(moduleType);

                var thisModuleRegistrations = new Dictionary<ITypeSymbol, Registration>();
                foreach (var (type, registration) in registrations)
                {
                    if (exclusionList.Contains(type))
                        continue;
                    if (directRegistrations.ContainsKey(type))
                        continue;
                    var use = true;
                    foreach (var (otherModuleRegistrationAttribute, otherModuleRegistrations) in moduleRegistrations)
                    {
                        if (otherModuleRegistrations.ContainsKey(type))
                        {
                            use = false;
                            _reportDiagnostic(RegisteredByMultipleModules(moduleRegistrationAttribute, moduleType, type, otherModuleRegistrationAttribute, _cancellationToken));
                            _reportDiagnostic(RegisteredByMultipleModules(otherModuleRegistrationAttribute, moduleType, type, otherModuleRegistrationAttribute, _cancellationToken));
                            break;
                        }
                    }
                    if (!use)
                        continue;

                    thisModuleRegistrations.Add(type, registration);
                }
                moduleRegistrations.Add((moduleRegistrationAttribute, thisModuleRegistrations));
            }

            return new Dictionary<ITypeSymbol, Registration>(directRegistrations.Concat(moduleRegistrations.SelectMany(x => x.registrations)));
        }

        private static Diagnostic RegisteredByMultipleModules(AttributeData attributeForLocation, INamedTypeSymbol moduleType, ITypeSymbol type, AttributeData otherModuleRegistrationAttribute, CancellationToken cancellationToken)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                    "SI0002",
                    "Type registered by multiple modules",
                    "'{0}' is registered by both modules '{1}' and '{2}'.",
                    "StrongInject",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                attributeForLocation.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None,
                type,
                otherModuleRegistrationAttribute.ConstructorArguments[0].Value,
                moduleType);
        }

        private Dictionary<ITypeSymbol, Registration> CalculateDirectRegistrations(ImmutableArray<AttributeData> attributes)
        {
            var directRegistrations = new Dictionary<ITypeSymbol, Registration>();
            foreach (var registrationAttribute in attributes.Where(x => x.AttributeClass?.Equals(_registrationAttributeType, SymbolEqualityComparer.Default) ?? false))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var countConstructorArguments = registrationAttribute.ConstructorArguments.Length;
                if (countConstructorArguments is not (2 or 3))
                {
                    // Invalid code, ignore;
                    continue;
                }

                var typeConstant = registrationAttribute.ConstructorArguments[0];
                if (typeConstant.Kind != TypedConstantKind.Type)
                {
                    // Invalid code, ignore;
                    continue;
                }
                if (typeConstant.Value is not INamedTypeSymbol type || type.ReferencesTypeParametersOrErrorTypes())
                {
                    _reportDiagnostic(InvalidType(
                        (ITypeSymbol)typeConstant.Value!,
                        registrationAttribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None));
                    continue;
                }
                else if (!type.IsPublic())
                {
                    _reportDiagnostic(TypeNotPublic(
                        (ITypeSymbol)typeConstant.Value!,
                        registrationAttribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None));
                    continue;
                }

                IMethodSymbol constructor;
                var applicableConstructors = type.Constructors.Where(x => x.DeclaredAccessibility == Accessibility.Public).ToList();
                if (applicableConstructors.Count == 0)
                {
                    _reportDiagnostic(NoConstructor(registrationAttribute, type, _cancellationToken));
                    continue;
                }
                else if (applicableConstructors.Count == 1)
                {
                    constructor = applicableConstructors[0];
                }
                else
                {
                    var nonDefaultConstructors = applicableConstructors.Where(x => x.Parameters.Length != 0).ToList();
                    if (nonDefaultConstructors.Count == 0)
                    {
                        // We should only be able to get here in an error case. Take the first constructor.
                        constructor = applicableConstructors[0];
                    }
                    else if (nonDefaultConstructors.Count == 1)
                    {
                        constructor = nonDefaultConstructors[0];
                    }
                    else
                    {
                        _reportDiagnostic(MultipleConstructors(registrationAttribute, type, _cancellationToken));
                        continue;
                    }
                }

                if (constructor.Parameters.Any(x => x.Type is not INamedTypeSymbol))
                {
                    _reportDiagnostic(ConstructorParameterNonNamedTypeSymbol(registrationAttribute, type, constructor, _cancellationToken));
                    continue;
                }

                var lifeTime = countConstructorArguments == 3 && registrationAttribute.ConstructorArguments[1] is { Kind: TypedConstantKind.Enum, Value: int value }
                    ? (Lifetime)value
                    : Lifetime.InstancePerDependency;

                var registeredAsConstants = registrationAttribute.ConstructorArguments[countConstructorArguments - 1].Values;
                var registeredAs = registeredAsConstants.IsDefaultOrEmpty ? new[] { type } : registeredAsConstants.Select(x => x.Value).OfType<INamedTypeSymbol>().ToArray();

                var interfacesAndBaseTypes = type.GetBaseTypesAndThis().Concat(type.AllInterfaces).ToHashSet(SymbolEqualityComparer.Default);
                foreach (var directTarget in registeredAs)
                {
                    if (directTarget.ReferencesTypeParametersOrErrorTypes())
                    {
                        _reportDiagnostic(InvalidType(
                            directTarget,
                            registrationAttribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None));
                        continue;
                    }
                    else if (!directTarget.IsPublic())
                    {
                        _reportDiagnostic(TypeNotPublic(
                            (ITypeSymbol)typeConstant.Value!,
                            registrationAttribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None));
                        continue;
                    }

                    if (!interfacesAndBaseTypes.Contains(directTarget))
                    {
                        _reportDiagnostic(DoesNotImplement(registrationAttribute, type, directTarget, _cancellationToken));
                        continue;
                    }

                    var requiresInitialization = interfacesAndBaseTypes.Contains(_iRequiresInitializationType);
                    if (directRegistrations.ContainsKey(directTarget))
                    {
                        _reportDiagnostic(DuplicateRegistration(registrationAttribute, directTarget, _cancellationToken));
                        continue;
                    }

                    directRegistrations.Add(directTarget, new Registration(type, directTarget, lifeTime, directTarget, isFactory: false, requiresInitialization, constructor));

                    if (directTarget.OriginalDefinition.Equals(_iFactoryType, SymbolEqualityComparer.Default))
                    {
                        var factoryTarget = directTarget.TypeArguments.First();

                        if (directRegistrations.ContainsKey(factoryTarget))
                        {
                            _reportDiagnostic(DuplicateRegistration(registrationAttribute, factoryTarget, _cancellationToken));
                            continue;
                        }

                        directRegistrations.Add(factoryTarget, new Registration(type, factoryTarget, lifeTime, directTarget, isFactory: true, requiresInitialization, constructor));
                    }
                }
            }
            return directRegistrations;
        }

        private static Diagnostic ConstructorParameterNonNamedTypeSymbol(AttributeData registrationAttribute, INamedTypeSymbol type, IMethodSymbol constructor, CancellationToken cancellationToken)
        {
            return
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "SI0008",
                        "Constructor has parameter not of named type symbol",
                        "'{0}' does not have any public constructors.",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    registrationAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None,
                    type);
        }

        private static Diagnostic NoConstructor(AttributeData registrationAttribute, INamedTypeSymbol type, CancellationToken cancellationToken)
        {
            return
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "SI0005",
                        "Registered type does not have any public constructors",
                        "'{0}' does not have any public constructors.",
                        "StrongInject",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    registrationAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None,
                    type);
        }

        private static Diagnostic MultipleConstructors(AttributeData registrationAttribute, INamedTypeSymbol type, CancellationToken cancellationToken)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                    "SI0006",
                    "Registered type has multiple non-default public constructors",
                    "'{0}' has multiple non-default public constructors.",
                    "StrongInject",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                registrationAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None,
                type);
        }

        private static Diagnostic DoesNotImplement(AttributeData registrationAttribute, INamedTypeSymbol registeredType, INamedTypeSymbol registeredAsType, CancellationToken cancellationToken)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                    "SI0001",
                    "Registered type does not implement registered as type",
                    "'{0}' does not implement '{1}'.",
                    "StrongInject",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                registrationAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None,
                registeredType,
                registeredAsType);
        }

        private static Diagnostic DuplicateRegistration(AttributeData registrationAttribute, ITypeSymbol registeredAsType, CancellationToken cancellationToken)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                    "SI0004",
                    "Module already contains registration for type",
                    "Module already contains registration for '{0}'.",
                    "StrongInject",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                registrationAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None,
                registeredAsType);
        }

        private static Diagnostic InvalidType(ITypeSymbol typeSymbol, Location location)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                    "SI0003",
                    "Type is invalid in a registration",
                    "'{0}' is invalid in a registration.",
                    "StrongInject",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                location,
                typeSymbol);
        }

        private static Diagnostic TypeNotPublic(ITypeSymbol typeSymbol, Location location)
        {
            return Diagnostic.Create(
                new DiagnosticDescriptor(
                    "SI0007",
                    "Type is not public",
                    "'{0}' is not public.",
                    "StrongInject",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true),
                location,
                typeSymbol);
        }
    }
}
