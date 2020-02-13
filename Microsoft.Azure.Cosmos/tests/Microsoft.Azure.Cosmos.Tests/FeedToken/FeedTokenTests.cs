﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.Query.Core.ContinuationTokens;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Moq;

    [TestClass]
    public class FeedTokenTests
    {
        [TestMethod]
        public void FeedToken_EPK_MoveToNextTokenCircles()
        {
            const string containerRid = "containerRid";
            List<Documents.PartitionKeyRange> keyRanges = new List<Documents.PartitionKeyRange>()
            {
                new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive ="B" },
                new Documents.PartitionKeyRange() { MinInclusive = "D", MaxExclusive ="E" },
            };
            FeedTokenEPKRange token = new FeedTokenEPKRange(containerRid, keyRanges);
            Assert.AreEqual(keyRanges[0].MinInclusive, token.CompositeContinuationTokens.Peek().Range.Min);
            token.UpdateContinuation("something");
            Assert.AreEqual(keyRanges[1].MinInclusive, token.CompositeContinuationTokens.Peek().Range.Min);
            token.UpdateContinuation("something");
            Assert.AreEqual(keyRanges[0].MinInclusive, token.CompositeContinuationTokens.Peek().Range.Min);
            token.UpdateContinuation("something");
            Assert.AreEqual(keyRanges[1].MinInclusive, token.CompositeContinuationTokens.Peek().Range.Min);
        }

        [TestMethod]
        public void FeedToken_EPK_TrySplit()
        {
            const string containerRid = "containerRid";
            List<Documents.PartitionKeyRange> keyRanges = new List<Documents.PartitionKeyRange>()
            {
                new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive ="B" },
                new Documents.PartitionKeyRange() { MinInclusive = "D", MaxExclusive ="E" },
            };
            FeedTokenEPKRange token = new FeedTokenEPKRange(containerRid, keyRanges);
            Assert.IsTrue(token.TrySplit(out IEnumerable<FeedToken> splitTokens));
            Assert.AreEqual(keyRanges.Count, splitTokens.Count());

            List<FeedTokenEPKRange> feedTokenEPKRanges = splitTokens.Select(t => t as FeedTokenEPKRange).ToList();
            Assert.AreEqual(keyRanges[0].MinInclusive, feedTokenEPKRanges[0].CompositeContinuationTokens.Peek().Range.Min);
            Assert.AreEqual(keyRanges[0].MaxExclusive, feedTokenEPKRanges[0].CompositeContinuationTokens.Peek().Range.Max);
            Assert.AreEqual(keyRanges[1].MinInclusive, feedTokenEPKRanges[1].CompositeContinuationTokens.Peek().Range.Min);
            Assert.AreEqual(keyRanges[1].MaxExclusive, feedTokenEPKRanges[1].CompositeContinuationTokens.Peek().Range.Max);
            Assert.AreEqual(keyRanges[0].MinInclusive, feedTokenEPKRanges[0].CompleteRange.Min);
            Assert.AreEqual(keyRanges[0].MaxExclusive, feedTokenEPKRanges[0].CompleteRange.Max);
            Assert.AreEqual(keyRanges[1].MinInclusive, feedTokenEPKRanges[1].CompleteRange.Min);
            Assert.AreEqual(keyRanges[1].MaxExclusive, feedTokenEPKRanges[1].CompleteRange.Max);

            FeedTokenEPKRange singleToken = new FeedTokenEPKRange(containerRid, new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive = "B" });
            Assert.IsFalse(singleToken.TrySplit(out IEnumerable<FeedToken> _));
        }

        [TestMethod]
        public void FeedToken_EPK_TryParse()
        {
            const string containerRid = "containerRid";
            List<Documents.PartitionKeyRange> keyRanges = new List<Documents.PartitionKeyRange>()
            {
                new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive ="B" },
                new Documents.PartitionKeyRange() { MinInclusive = "D", MaxExclusive ="E" },
            };
            FeedTokenEPKRange token = new FeedTokenEPKRange(containerRid, keyRanges);
            Assert.IsTrue(FeedTokenEPKRange.TryParseInstance(token.ToString(), out FeedToken parsed));
            Assert.IsFalse(FeedTokenEPKRange.TryParseInstance("whatever", out FeedToken _));
        }

        [TestMethod]
        public void FeedToken_EPK_FillHeaders()
        {
            const string containerRid = "containerRid";
            FeedTokenEPKRange token = new FeedTokenEPKRange(containerRid, new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive = "B" });
            string continuation = Guid.NewGuid().ToString();
            token.UpdateContinuation(continuation);
            RequestMessage requestMessage = new RequestMessage();
            Mock<CosmosClientContext> cosmosClientContext = new Mock<CosmosClientContext>();
            MultiRangeMockDocumentClient mockDocumentClient = new MultiRangeMockDocumentClient();
            cosmosClientContext.Setup(c => c.DocumentClient).Returns(new MultiRangeMockDocumentClient());
            token.FillHeaders(cosmosClientContext.Object, requestMessage);
            Assert.AreEqual(continuation, requestMessage.Headers.IfNoneMatch);
            Assert.AreEqual(mockDocumentClient.AvailablePartitionKeyRanges[0].Id, requestMessage.PartitionKeyRangeId.PartitionKeyRangeId);
        }

        [TestMethod]
        public void FeedToken_PartitionKey_TryParse()
        {
            FeedTokenPartitionKey token = new FeedTokenPartitionKey(new PartitionKey("test"));
            Assert.IsTrue(FeedTokenPartitionKey.TryParseInstance(token.ToString(), out FeedToken parsed));
            Assert.IsFalse(FeedTokenPartitionKey.TryParseInstance("whatever", out FeedToken _));
        }

        [TestMethod]
        public void FeedToken_PartitionKeyRange_TryParse()
        {
            FeedTokenPartitionKeyRange token = new FeedTokenPartitionKeyRange("0");
            Assert.IsTrue(FeedTokenPartitionKeyRange.TryParseInstance(token.ToString(), out FeedToken parsed));
            Assert.IsTrue(FeedTokenPartitionKeyRange.TryParseInstance("1", out FeedToken _));
            Assert.IsFalse(FeedTokenPartitionKey.TryParseInstance("whatever", out FeedToken _));
        }

        [TestMethod]
        public void FeedToken_PartitionKey_FillHeader()
        {
            PartitionKey pk = new PartitionKey("test");
            FeedTokenPartitionKey token = new FeedTokenPartitionKey(pk);
            RequestMessage requestMessage = new RequestMessage();
            string continuation = Guid.NewGuid().ToString();
            token.UpdateContinuation(continuation);
            token.FillHeaders(Mock.Of<CosmosClientContext>(), requestMessage);
            Assert.AreEqual(continuation, requestMessage.Headers.IfNoneMatch);
            Assert.AreEqual(pk.ToJsonString(), requestMessage.Headers.PartitionKey);
        }

        [TestMethod]
        public void FeedToken_PartitionKeyRange_FillHeader()
        {
            string pkrangeId = "0";
            FeedTokenPartitionKeyRange token = new FeedTokenPartitionKeyRange(pkrangeId);
            RequestMessage requestMessage = new RequestMessage();
            string continuation = Guid.NewGuid().ToString();
            token.UpdateContinuation(continuation);
            token.FillHeaders(Mock.Of<CosmosClientContext>(), requestMessage);
            Assert.AreEqual(continuation, requestMessage.Headers.IfNoneMatch);
            Assert.AreEqual(pkrangeId, requestMessage.PartitionKeyRangeId.PartitionKeyRangeId);
        }

        [TestMethod]
        public void FeedToken_EPK_CompleteRange()
        {
            const string containerRid = "containerRid";
            List<Documents.PartitionKeyRange> keyRanges = new List<Documents.PartitionKeyRange>()
            {
                new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive ="B" },
                new Documents.PartitionKeyRange() { MinInclusive = "D", MaxExclusive ="E" },
            };
            FeedTokenEPKRange token = new FeedTokenEPKRange(containerRid, keyRanges);
            Assert.AreEqual(keyRanges.Count, token.CompositeContinuationTokens.Count);
            Assert.AreEqual(keyRanges[0].MinInclusive, token.CompleteRange.Min);
            Assert.AreEqual(keyRanges[1].MaxExclusive, token.CompleteRange.Max);
        }

        [TestMethod]
        public void FeedToken_EPK_SingleRange()
        {
            const string containerRid = "containerRid";
            Documents.PartitionKeyRange partitionKeyRange = new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive = "B" };
            FeedTokenEPKRange token = new FeedTokenEPKRange(containerRid, partitionKeyRange);
            Assert.AreEqual(1, token.CompositeContinuationTokens.Count);
            Assert.AreEqual(partitionKeyRange.MinInclusive, token.CompleteRange.Min);
            Assert.AreEqual(partitionKeyRange.MaxExclusive, token.CompleteRange.Max);
        }

        [TestMethod]
        public async Task FeedToken_EPK_ShouldRetry()
        {
            List<CompositeContinuationToken> compositeContinuationTokens = new List<CompositeContinuationToken>()
            {
                FeedTokenTests.BuildTokenForRange("A", "C", "token1"),
                FeedTokenTests.BuildTokenForRange("C", "F", "token2")
            };

            FeedTokenEPKRange feedTokenEPKRange = new FeedTokenEPKRange(Guid.NewGuid().ToString(), new Documents.Routing.Range<string>(compositeContinuationTokens[0].Range.Min, compositeContinuationTokens[1].Range.Min, true, false), compositeContinuationTokens);

            ContainerCore containerCore = Mock.Of<ContainerCore>();

            Assert.IsFalse(await feedTokenEPKRange.ShouldRetryAsync(containerCore, new ResponseMessage(HttpStatusCode.OK)));

            // A 304 on a multi Range token should cycle on all available ranges before stopping retrying
            Assert.IsTrue(await feedTokenEPKRange.ShouldRetryAsync(containerCore, new ResponseMessage(HttpStatusCode.NotModified)));
            feedTokenEPKRange.UpdateContinuation(Guid.NewGuid().ToString());
            Assert.IsTrue(await feedTokenEPKRange.ShouldRetryAsync(containerCore, new ResponseMessage(HttpStatusCode.NotModified)));
            feedTokenEPKRange.UpdateContinuation(Guid.NewGuid().ToString());
            Assert.IsFalse(await feedTokenEPKRange.ShouldRetryAsync(containerCore, new ResponseMessage(HttpStatusCode.NotModified)));
        }

        [TestMethod]
        public async Task FeedToken_EPK_HandleSplits()
        {
            List<CompositeContinuationToken> compositeContinuationTokens = new List<CompositeContinuationToken>()
            {
                FeedTokenTests.BuildTokenForRange("A", "C", "token1"),
                FeedTokenTests.BuildTokenForRange("C", "F", "token2")
            };

            FeedTokenEPKRange feedTokenEPKRange = new FeedTokenEPKRange(Guid.NewGuid().ToString(), new Documents.Routing.Range<string>(compositeContinuationTokens[0].Range.Min, compositeContinuationTokens[1].Range.Min, true, false), compositeContinuationTokens);

            MultiRangeMockDocumentClient documentClient = new MultiRangeMockDocumentClient();

            Mock<CosmosClientContext> cosmosClientContext = new Mock<CosmosClientContext>();
            cosmosClientContext.Setup(c => c.ClientOptions).Returns(new CosmosClientOptions());
            cosmosClientContext.Setup(c => c.DocumentClient).Returns(documentClient);

            Mock<ContainerCore> containerCore = new Mock<ContainerCore>();
            containerCore
                .Setup(c => c.ClientContext).Returns(cosmosClientContext.Object);

            Assert.AreEqual(2, feedTokenEPKRange.CompositeContinuationTokens.Count);

            ResponseMessage split = new ResponseMessage(HttpStatusCode.Gone);
            split.Headers.SubStatusCode = Documents.SubStatusCodes.PartitionKeyRangeGone;
            Assert.IsTrue(await feedTokenEPKRange.ShouldRetryAsync(containerCore.Object, split));

            // verify token state
            // Split should have updated initial and created a new token at the end
            Assert.AreEqual(3, feedTokenEPKRange.CompositeContinuationTokens.Count);
            CompositeContinuationToken[] continuationTokens = feedTokenEPKRange.CompositeContinuationTokens.ToArray();
            // First token is split
            Assert.AreEqual(compositeContinuationTokens[0].Token, continuationTokens[0].Token);
            Assert.AreEqual(documentClient.AvailablePartitionKeyRanges[0].MinInclusive, continuationTokens[0].Range.Min);
            Assert.AreEqual(documentClient.AvailablePartitionKeyRanges[0].MaxExclusive, continuationTokens[0].Range.Max);

            // Second token remains the same
            Assert.AreEqual(compositeContinuationTokens[1].Token, continuationTokens[1].Token);
            Assert.AreEqual(compositeContinuationTokens[1].Range.Min, continuationTokens[1].Range.Min);
            Assert.AreEqual(compositeContinuationTokens[1].Range.Max, continuationTokens[1].Range.Max);

            // New third token
            Assert.AreEqual(compositeContinuationTokens[0].Token, continuationTokens[2].Token);
            Assert.AreEqual(documentClient.AvailablePartitionKeyRanges[1].MinInclusive, continuationTokens[2].Range.Min);
            Assert.AreEqual(documentClient.AvailablePartitionKeyRanges[1].MaxExclusive, continuationTokens[2].Range.Max);
        }

        private static CompositeContinuationToken BuildTokenForRange(
            string min,
            string max,
            string token)
        {
            return new CompositeContinuationToken()
            {
                Token = token,
                Range = new Documents.Routing.Range<string>(min, max, true, false)
            };
        }

        private class MultiRangeMockDocumentClient : MockDocumentClient
        {
            public List<Documents.PartitionKeyRange> AvailablePartitionKeyRanges = new List<Documents.PartitionKeyRange>() {
                new Documents.PartitionKeyRange() { MinInclusive = "A", MaxExclusive ="B", Id = "0" },
                new Documents.PartitionKeyRange() { MinInclusive = "B", MaxExclusive ="C", Id = "0" },
                new Documents.PartitionKeyRange() { MinInclusive = "C", MaxExclusive ="F", Id = "0" },
            };

            internal override IReadOnlyList<Documents.PartitionKeyRange> ResolveOverlapingPartitionKeyRanges(string collectionRid, Documents.Routing.Range<string> range, bool forceRefresh)
            {
                return new List<Documents.PartitionKeyRange>() { this.AvailablePartitionKeyRanges[0], this.AvailablePartitionKeyRanges[1] };
            }
        }
    }
}