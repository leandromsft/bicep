// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using Azure.Deployments.Core.Json;
using Bicep.Core.UnitTests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Newtonsoft.Json.Linq;
using Bicep.Core.UnitTests;
using Bicep.LangServer.IntegrationTests.Helpers;
using FluentAssertions;
using Bicep.Core.Samples;
using Bicep.Core.UnitTests.Assertions;
using Moq;
using Bicep.LanguageServer.Providers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Bicep.LanguageServer.Handlers;
using Bicep.LanguageServer.Utils;
using Bicep.Core.Text;
using Azure.Deployments.Core.Definitions.Identifiers;
using Bicep.Core.Configuration;
using System.Threading;
using System.Text.Json;
using System.Text;
using Bicep.Core.TypeSystem.Az;
using Bicep.Core.TypeSystem;
using Bicep.Core;
using Bicep.Core.Extensions;
using System.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using System;

namespace Bicep.LangServer.IntegrationTests
{
    [TestClass]
    public class InsertResourceCommandTests
    {
        [NotNull]
        public TestContext? TestContext { get; set; }

        private async Task<LanguageServerHelper> StartLanguageServer(
            MultipleMessageListener<PublishDiagnosticsParams> diagnosticsListener,
            MultipleMessageListener<ShowMessageParams> showMessageListener,
            MultipleMessageListener<ApplyWorkspaceEditParams> workspaceEditsListener,
            IAzResourceProvider azResourceProvider,
            IAzResourceTypeLoader azResourceTypeLoader)
        {
            return await LanguageServerHelper.StartServerWithClientConnectionAsync(
                this.TestContext,
                options => {
                    options.OnPublishDiagnostics(x => diagnosticsListener.AddMessage(x));
                    options.OnShowMessage(x => showMessageListener.AddMessage(x));
                    options.OnApplyWorkspaceEdit(async p => {
                        workspaceEditsListener.AddMessage(p);

                        await Task.Yield();
                        return new();
                    });
                },
                new LanguageServer.Server.CreationOptions(
                    onRegisterServices: services => {
                        services.AddSingleton<IAzResourceProvider>(azResourceProvider);
                        services.AddSingleton<IAzResourceTypeLoader>(azResourceTypeLoader);
                    }));
        }

        [TestMethod]
        public async Task Insert_resource_command_should_insert_basic_resource()
        {
            var documentUri = DocumentUri.From("/template.bicep");
            var diagsReceived = new TaskCompletionSource<PublishDiagnosticsParams>();

            var diagnosticsListener = new MultipleMessageListener<PublishDiagnosticsParams>();
            var showMessageListener = new MultipleMessageListener<ShowMessageParams>();
            var workspaceEditsListener = new MultipleMessageListener<ApplyWorkspaceEditParams>();
            var mockAzResourceProvider = new Mock<IAzResourceProvider>(MockBehavior.Strict);
            
            var typeDefinition = TestTypeHelper.CreateCustomResourceType("My.Rp/myTypes", "2020-01-01", TypeSymbolValidationFlags.WarnOnTypeMismatch,
                new TypeProperty("readOnlyProp", LanguageConstants.String, TypePropertyFlags.ReadOnly),
                new TypeProperty("readWriteProp", LanguageConstants.String, TypePropertyFlags.None),
                new TypeProperty("writeOnlyProp", LanguageConstants.String, TypePropertyFlags.WriteOnly));
            var typeLoader = TestTypeHelper.CreateAzResourceTypeLoaderWithTypes(typeDefinition.AsEnumerable());
            
            using var helper = await StartLanguageServer(diagnosticsListener, showMessageListener, workspaceEditsListener, mockAzResourceProvider.Object, typeLoader);
            var client = helper.Client;

            var resourceId = ResourceGroupLevelResourceId.Create("23775d31-d753-4290-805b-e5bde53eba6e", "myRg", "My.Rp", new [] { "myTypes"}, new [] { "myName"});
            var mockResource = new JObject
            {
                ["id"] = resourceId.FullyQualifiedId,
                ["name"] = resourceId.NameHierarchy.Last(),
                ["type"] = resourceId.FormatFullyQualifiedType(),
                ["properties"] = new JObject
                {
                    ["readOnlyProp"] = "abc",
                    ["readWriteProp"] = "def",
                    ["writeOnlyProp"] = "ghi",
                },
            };

            mockAzResourceProvider.Setup(x => x.GetGenericResource(It.IsAny<RootConfiguration>(), resourceId, "2020-01-01", It.IsAny<CancellationToken>()))
                .Returns(async () => await JsonSerializer.DeserializeAsync<JsonElement>(mockResource.ToJsonStream()));

            var (file, cursors) = ParserHelper.GetFileWithCursors(@"
param myParam string = 'test'
resource myRes 'myRp/provider@2019-01-01' = {
  name: 'te|st'
}
module myMod './module.bicep' = {
  name: 'test' 
}
output myOutput string = 'myOutput'
");
            var lineStarts = TextCoordinateConverter.GetLineStarts(file);

            client.TextDocument.DidOpenTextDocument(TextDocumentParamHelper.CreateDidOpenDocumentParams(documentUri, file, 0));
            await diagnosticsListener.WaitNext();

            var cursor = cursors.Single();
            var result = await client.SendRequest(new InsertResourceParams
            {
                TextDocument = documentUri,
                Position = PositionHelper.GetPosition(lineStarts, cursor),
                ResourceId = resourceId.FullyQualifiedId,
            }, default);

            var edit = await workspaceEditsListener.WaitNext();

            var changes = edit.Edit.Changes![documentUri];
            changes.Should().HaveCount(1);
            var test = changes.First();
            
            var startOffset = PositionHelper.GetOffset(lineStarts, test.Range.Start);
            var endOffset = PositionHelper.GetOffset(lineStarts, test.Range.End);

            var replacedFile = file.Substring(0, startOffset) + test.NewText + file.Substring(endOffset);

            replacedFile.Should().Be(@"
param myParam string = 'test'
resource myRes 'myRp/provider@2019-01-01' = {
  name: 'test'
}
resource myName 'My.Rp/myTypes@2020-01-01' = {
  name: 'myName'
  properties: {
    readWriteProp: 'def'
    writeOnlyProp: 'ghi'
  }
}
module myMod './module.bicep' = {
  name: 'test' 
}
output myOutput string = 'myOutput'
");
        }

        [TestMethod]
        public async Task Insert_resource_command_displays_error_for_incorrectly_formatted_resourceId()
        {
            var documentUri = DocumentUri.From("/template.bicep");
            var diagsReceived = new TaskCompletionSource<PublishDiagnosticsParams>();

            var diagnosticsListener = new MultipleMessageListener<PublishDiagnosticsParams>();
            var showMessageListener = new MultipleMessageListener<ShowMessageParams>();
            var workspaceEditsListener = new MultipleMessageListener<ApplyWorkspaceEditParams>();
            var mockAzResourceProvider = new Mock<IAzResourceProvider>(MockBehavior.Strict);
            
            var typeDefinition = TestTypeHelper.CreateCustomResourceType("My.Rp/myTypes", "2020-01-01", TypeSymbolValidationFlags.WarnOnTypeMismatch,
                new TypeProperty("readOnlyProp", LanguageConstants.String, TypePropertyFlags.ReadOnly),
                new TypeProperty("readWriteProp", LanguageConstants.String, TypePropertyFlags.None),
                new TypeProperty("writeOnlyProp", LanguageConstants.String, TypePropertyFlags.WriteOnly));
            var typeLoader = TestTypeHelper.CreateAzResourceTypeLoaderWithTypes(typeDefinition.AsEnumerable());
            
            using var helper = await StartLanguageServer(diagnosticsListener, showMessageListener, workspaceEditsListener, mockAzResourceProvider.Object, typeLoader);
            var client = helper.Client;

            var (file, cursors) = ParserHelper.GetFileWithCursors(@"
param myParam string = 'test'
resource myRes 'myRp/provider@2019-01-01' = {
  name: 'te|st'
}
module myMod './module.bicep' = {
  name: 'test' 
}
output myOutput string = 'myOutput'
");
            var lineStarts = TextCoordinateConverter.GetLineStarts(file);

            client.TextDocument.DidOpenTextDocument(TextDocumentParamHelper.CreateDidOpenDocumentParams(documentUri, file, 0));
            await diagnosticsListener.WaitNext();

            var cursor = cursors.Single();
            var result = await client.SendRequest(new InsertResourceParams
            {
                TextDocument = documentUri,
                Position = PositionHelper.GetPosition(lineStarts, cursor),
                ResourceId = "this isn't a resource id!",
            }, default);

            var message = await showMessageListener.WaitNext();

            message.Message.Should().Be("Failed to parse supplied resourceId \"this isn't a resource id!\".");
            message.Type.Should().Be(MessageType.Error);
        }

        [TestMethod]
        public async Task Insert_resource_command_displays_error_for_resource_with_no_types()
        {
            var documentUri = DocumentUri.From("/template.bicep");
            var diagsReceived = new TaskCompletionSource<PublishDiagnosticsParams>();

            var diagnosticsListener = new MultipleMessageListener<PublishDiagnosticsParams>();
            var showMessageListener = new MultipleMessageListener<ShowMessageParams>();
            var workspaceEditsListener = new MultipleMessageListener<ApplyWorkspaceEditParams>();
            var mockAzResourceProvider = new Mock<IAzResourceProvider>(MockBehavior.Strict);
            
            var typeLoader = TestTypeHelper.CreateEmptyAzResourceTypeLoader();
            
            using var helper = await StartLanguageServer(diagnosticsListener, showMessageListener, workspaceEditsListener, mockAzResourceProvider.Object, typeLoader);
            var client = helper.Client;

            var resourceId = ResourceGroupLevelResourceId.Create("23775d31-d753-4290-805b-e5bde53eba6e", "myRg", "MadeUp.Rp", new [] { "madeUpTypes"}, new [] { "myName"});

            var (file, cursors) = ParserHelper.GetFileWithCursors(@"
param myParam string = 'test'
resource myRes 'myRp/provider@2019-01-01' = {
  name: 'te|st'
}
module myMod './module.bicep' = {
  name: 'test' 
}
output myOutput string = 'myOutput'
");
            var lineStarts = TextCoordinateConverter.GetLineStarts(file);

            client.TextDocument.DidOpenTextDocument(TextDocumentParamHelper.CreateDidOpenDocumentParams(documentUri, file, 0));
            await diagnosticsListener.WaitNext();

            var cursor = cursors.Single();
            var result = await client.SendRequest(new InsertResourceParams
            {
                TextDocument = documentUri,
                Position = PositionHelper.GetPosition(lineStarts, cursor),
                ResourceId = resourceId.FullyQualifiedId,
            }, default);

            var message = await showMessageListener.WaitNext();

            message.Message.Should().Be("Failed to find a Bicep type definition for resource of type \"MadeUp.Rp/madeUpTypes\".");
            message.Type.Should().Be(MessageType.Error);
        }

        [TestMethod]
        public async Task Insert_resource_command_should_return_exception_info()
        {
            var documentUri = DocumentUri.From("/template.bicep");
            var diagsReceived = new TaskCompletionSource<PublishDiagnosticsParams>();

            var diagnosticsListener = new MultipleMessageListener<PublishDiagnosticsParams>();
            var showMessageListener = new MultipleMessageListener<ShowMessageParams>();
            var workspaceEditsListener = new MultipleMessageListener<ApplyWorkspaceEditParams>();
            var mockAzResourceProvider = new Mock<IAzResourceProvider>(MockBehavior.Strict);
            
            var typeDefinition = TestTypeHelper.CreateCustomResourceType("My.Rp/myTypes", "2020-01-01", TypeSymbolValidationFlags.WarnOnTypeMismatch,
                new TypeProperty("readOnlyProp", LanguageConstants.String, TypePropertyFlags.ReadOnly),
                new TypeProperty("readWriteProp", LanguageConstants.String, TypePropertyFlags.None),
                new TypeProperty("writeOnlyProp", LanguageConstants.String, TypePropertyFlags.WriteOnly));
            var typeLoader = TestTypeHelper.CreateAzResourceTypeLoaderWithTypes(typeDefinition.AsEnumerable());
            
            using var helper = await StartLanguageServer(diagnosticsListener, showMessageListener, workspaceEditsListener, mockAzResourceProvider.Object, typeLoader);
            var client = helper.Client;

            var resourceId = ResourceGroupLevelResourceId.Create("23775d31-d753-4290-805b-e5bde53eba6e", "myRg", "My.Rp", new [] { "myTypes"}, new [] { "myName"});

            mockAzResourceProvider.Setup(x => x.GetGenericResource(It.IsAny<RootConfiguration>(), resourceId, "2020-01-01", It.IsAny<CancellationToken>()))
                .Throws(new InvalidOperationException("Something went wrong!"));

            var (file, cursors) = ParserHelper.GetFileWithCursors(@"
param myParam string = 'test'
resource myRes 'myRp/provider@2019-01-01' = {
  name: 'te|st'
}
module myMod './module.bicep' = {
  name: 'test' 
}
output myOutput string = 'myOutput'
");
            var lineStarts = TextCoordinateConverter.GetLineStarts(file);

            client.TextDocument.DidOpenTextDocument(TextDocumentParamHelper.CreateDidOpenDocumentParams(documentUri, file, 0));
            await diagnosticsListener.WaitNext();

            var cursor = cursors.Single();
            var result = await client.SendRequest(new InsertResourceParams
            {
                TextDocument = documentUri,
                Position = PositionHelper.GetPosition(lineStarts, cursor),
                ResourceId = resourceId.FullyQualifiedId,
            }, default);

            var message = await showMessageListener.WaitNext();

            message.Message.Should().Be("Caught exception inserting resource: Something went wrong!.");
            message.Type.Should().Be(MessageType.Error);
        }
    }
}
