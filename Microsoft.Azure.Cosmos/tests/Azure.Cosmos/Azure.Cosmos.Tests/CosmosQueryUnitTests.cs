﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Azure.Cosmos.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Cosmos.Query;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Query;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionComponent;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Documents;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Moq;

    [TestClass]
    public class CosmosQueryUnitTests
    {
        [TestMethod]
        public void VerifyCosmosQueryResponseStream()
        {
            string contianerRid = "mockContainerRid";
            (QueryResponseCore response, IList<ToDoItem> items) factoryResponse = QueryResponseMessageFactory.Create(
                        itemIdPrefix: $"TestPage",
                        continuationToken: "SomeContinuationToken",
                        collectionRid: contianerRid,
                        itemCount: 100);

            QueryResponseCore responseCore = factoryResponse.response;

            QueryResponse queryResponse = QueryResponse.CreateSuccess(
                        result: responseCore.CosmosElements,
                        count: responseCore.CosmosElements.Count,
                        responseLengthBytes: responseCore.ResponseLengthBytes,
                        responseHeaders: new CosmosQueryResponseMessageHeaders(
                            responseCore.ContinuationToken,
                            responseCore.DisallowContinuationTokenMessage,
                            ResourceType.Document,
                            contianerRid)
                        {
                            RequestCharge = responseCore.RequestCharge,
                            ActivityId = responseCore.ActivityId
                        });

            using (Stream stream = queryResponse.Content)
            {
                using (Stream innerStream = queryResponse.Content)
                {
                    Assert.IsTrue(object.ReferenceEquals(stream, innerStream), "Content should return the same stream");
                }
            }
        }

        [TestMethod]
        public async Task TestCosmosQueryExecutionComponentOnFailure()
        {
            (IList<IDocumentQueryExecutionComponent> components, QueryResponseCore response) setupContext = await this.GetAllExecutionComponents();

            foreach (DocumentQueryExecutionComponentBase component in setupContext.components)
            {
                QueryResponseCore response = await component.DrainAsync(1, default(CancellationToken));
                Assert.AreEqual(setupContext.response, response);
            }
        }

        [TestMethod]
        public async Task TestCosmosQueryExecutionComponentCancellation()
        {
            (IList<IDocumentQueryExecutionComponent> components, QueryResponseCore response) setupContext = await this.GetAllExecutionComponents();
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            foreach (DocumentQueryExecutionComponentBase component in setupContext.components)
            {
                try
                {
                    QueryResponseCore response = await component.DrainAsync(1, cancellationTokenSource.Token);
                    Assert.Fail("cancellation token should have thrown an exception");
                }
                catch (OperationCanceledException e)
                {
                    Assert.IsNotNull(e.Message);
                }
            }
        }

        [TestMethod]
        [ExpectedException(typeof(CosmosException))]
        public async Task TestCosmosQueryPartitionKeyDefinition()
        {
            PartitionKeyDefinition partitionKeyDefinition = new PartitionKeyDefinition();
            QueryRequestOptions queryRequestOptions = new QueryRequestOptions
            {
                Properties = new Dictionary<string, object>()
            {
                {"x-ms-query-partitionkey-definition", partitionKeyDefinition }
            }
            };

            SqlQuerySpec sqlQuerySpec = new SqlQuerySpec(@"select * from t where t.something = 42 ");
            bool allowNonValueAggregateQuery = true;
            bool isContinuationExpected = true;
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            CancellationToken cancellationtoken = cancellationTokenSource.Token;

            Mock<CosmosQueryClient> client = new Mock<CosmosQueryClient>();
            client
                .Setup(x => x.GetCachedContainerQueryPropertiesAsync(It.IsAny<Uri>(), It.IsAny<Cosmos.PartitionKey?>(), cancellationtoken))
                .ReturnsAsync(new ContainerQueryProperties("mockContainer", null, partitionKeyDefinition));
            client
                .Setup(x => x.ByPassQueryParsing())
                .Returns(false);
            client
                .Setup(x => x.TryGetPartitionedQueryExecutionInfoAsync(
                    It.IsAny<SqlQuerySpec>(),
                    It.IsAny<PartitionKeyDefinition>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(TryCatch<PartitionedQueryExecutionInfo>.FromException(
                    new InvalidOperationException(
                        "Verified that the PartitionKeyDefinition was correctly set. Cancel the rest of the query")));

            CosmosQueryExecutionContextFactory.InputParameters inputParameters = new CosmosQueryExecutionContextFactory.InputParameters()
            {
                SqlQuerySpec = sqlQuerySpec,
                InitialUserContinuationToken = null,
                MaxBufferedItemCount = queryRequestOptions?.MaxBufferedItemCount,
                MaxConcurrency = queryRequestOptions?.MaxConcurrency,
                MaxItemCount = queryRequestOptions?.MaxItemCount,
                PartitionKey = queryRequestOptions?.PartitionKey,
                Properties = queryRequestOptions?.Properties
            };

            CosmosQueryContext cosmosQueryContext = new CosmosQueryContextCore(
                client: client.Object,
                queryRequestOptions: queryRequestOptions,
                resourceTypeEnum: ResourceType.Document,
                operationType: OperationType.Query,
                resourceType: typeof(QueryResponse),
                resourceLink: new Uri("dbs/mockdb/colls/mockColl", UriKind.Relative),
                isContinuationExpected: isContinuationExpected,
                allowNonValueAggregateQuery: allowNonValueAggregateQuery,
                correlatedActivityId: new Guid("221FC86C-1825-4284-B10E-A6029652CCA6"));

            CosmosQueryExecutionContextFactory factory = new CosmosQueryExecutionContextFactory(
                cosmosQueryContext: cosmosQueryContext,
                inputParameters: inputParameters);

            await factory.ExecuteNextAsync(cancellationtoken);
        }

        private async Task<(IList<IDocumentQueryExecutionComponent> components, QueryResponseCore response)> GetAllExecutionComponents()
        {
            (Func<string, Task<TryCatch<IDocumentQueryExecutionComponent>>> func, QueryResponseCore response) setupContext = this.SetupBaseContextToVerifyFailureScenario();

            List<IDocumentQueryExecutionComponent> components = new List<IDocumentQueryExecutionComponent>();
            List<AggregateOperator> operators = new List<AggregateOperator>()
        {
            AggregateOperator.Average,
            AggregateOperator.Count,
            AggregateOperator.Max,
            AggregateOperator.Min,
            AggregateOperator.Sum
        };

            components.Add((await AggregateDocumentQueryExecutionComponent.TryCreateAsync(
                Microsoft.Azure.Cosmos.Query.Core.ExecutionContext.ExecutionEnvironment.Client,
                operators.ToArray(),
                new Dictionary<string, AggregateOperator?>()
                {
                { "test", AggregateOperator.Count }
                },
                new List<string>() { "test" },
                false,
                null,
                setupContext.func)).Result);

            components.Add((await DistinctDocumentQueryExecutionComponent.TryCreateAsync(
                Microsoft.Azure.Cosmos.Query.Core.ExecutionContext.ExecutionEnvironment.Client,
                null,
                setupContext.func,
                DistinctQueryType.Ordered)).Result);

            components.Add((await SkipDocumentQueryExecutionComponent.TryCreateAsync(
                5,
                null,
                setupContext.func)).Result);

            components.Add((await TakeDocumentQueryExecutionComponent.TryCreateLimitDocumentQueryExecutionComponentAsync(
                5,
                null,
                setupContext.func)).Result);

            components.Add((await TakeDocumentQueryExecutionComponent.TryCreateTopDocumentQueryExecutionComponentAsync(
                5,
                null,
                setupContext.func)).Result);

            return (components, setupContext.response);
        }

        private (Func<string, Task<TryCatch<IDocumentQueryExecutionComponent>>>, QueryResponseCore) SetupBaseContextToVerifyFailureScenario()
        {
            IReadOnlyCollection<QueryPageDiagnostics> diagnostics = new List<QueryPageDiagnostics>()
        {
            new QueryPageDiagnostics(
                "0",
                "SomeQueryMetricText",
                "SomeIndexUtilText",
            new PointOperationStatistics(
                Guid.NewGuid().ToString(),
                System.Net.HttpStatusCode.Unauthorized,
                subStatusCode: SubStatusCodes.PartitionKeyMismatch,
                requestCharge: 4,
                errorMessage: null,
                method: HttpMethod.Post,
                requestUri: new Uri("http://localhost.com"),
                clientSideRequestStatistics: null),
            new SchedulingStopwatch())
        };

            QueryResponseCore failure = QueryResponseCore.CreateFailure(
                System.Net.HttpStatusCode.Unauthorized,
                SubStatusCodes.PartitionKeyMismatch,
                "Random error message",
                42.89,
                "TestActivityId",
                diagnostics);

            Mock<IDocumentQueryExecutionComponent> baseContext = new Mock<IDocumentQueryExecutionComponent>();
            baseContext.Setup(x => x.DrainAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult<QueryResponseCore>(failure));
            Func<string, Task<TryCatch<IDocumentQueryExecutionComponent>>> callBack = x => Task.FromResult<TryCatch<IDocumentQueryExecutionComponent>>(TryCatch<IDocumentQueryExecutionComponent>.FromResult(baseContext.Object));
            return (callBack, failure);
        }
    }
}