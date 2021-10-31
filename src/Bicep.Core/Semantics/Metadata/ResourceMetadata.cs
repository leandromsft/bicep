// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Bicep.Core.Resources;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.Semantics.Namespaces;
using Bicep.Core.TypeSystem.Az;
using System.Collections.Immutable;

namespace Bicep.Core.Semantics.Metadata
{
    public record ResourceMetadata(
        ResourceType Type,
        ResourceSymbol Symbol,
        ResourceMetadataParent? Parent,
        ImmutableDictionary<string, SyntaxBase> Identifiers,
        SyntaxBase? ScopeSyntax,
        bool IsExistingResource)
    {
        public ResourceTypeReference TypeReference => Type.TypeReference;

        public SyntaxBase NameSyntax => Identifiers[AzResourceTypeProvider.ResourceNamePropertyName];

        public bool IsAzResource => Type.DeclaringNamespace.ProviderNameEquals(AzNamespaceType.BuiltInName);
    }
}
