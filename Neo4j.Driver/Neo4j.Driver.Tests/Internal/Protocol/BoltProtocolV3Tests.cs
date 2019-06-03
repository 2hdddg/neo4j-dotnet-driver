﻿// Copyright (c) 2002-2019 Neo4j Sweden AB [http://neo4j.com]
// 
// This file is part of Neo4j.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Neo4j.Driver.Internal.Connector;
using Neo4j.Driver.Internal.MessageHandling;
using Neo4j.Driver.Internal.Messaging;
using Neo4j.Driver.Internal.Messaging.V3;
using Neo4j.Driver.Internal.Result;
using Neo4j.Driver.Tests;
using Xunit;
using static Neo4j.Driver.Internal.Protocol.BoltProtocolUtils;
using static Neo4j.Driver.Internal.Protocol.BoltProtocolV3;
using V1 = Neo4j.Driver.Internal.MessageHandling.V1;
using V3 = Neo4j.Driver.Internal.MessageHandling.V3;

namespace Neo4j.Driver.Internal.Protocol
{
    public class BoltProtocolV3Tests
    {
        internal static readonly TransactionConfig TxConfig = new TransactionConfig
        {
            Timeout = TimeSpan.FromMinutes(1),
            Metadata = new Dictionary<string, object> {{"key1", "value1"}}
        };

        internal static readonly Bookmark Bookmark = Internal.Bookmark.From(SessionTests.FakeABookmark(123));

        internal static bool VerifyMetadata(IDictionary<string, object> metadata, AccessMode mode)
        {
            var keys = new List<string> {"bookmarks", "tx_timeout", "tx_metadata"};
            if (mode == AccessMode.Read)
            {
                keys.Add("mode");
            }

            metadata.Should().HaveCount(keys.Count).And.ContainKeys(keys);
            metadata["bookmarks"].Should().BeOfType<string[]>().Which.Should().HaveCount(1).And
                .Contain("neo4j:bookmark:v1:tx123");
            metadata["tx_timeout"].Should().Be(60000L);
            metadata["tx_metadata"].Should().BeOfType<Dictionary<string, object>>().Which.Should().HaveCount(1).And
                .Contain(new KeyValuePair<string, object>("key1", "value1"));
            if (mode == AccessMode.Read)
            {
                metadata["mode"].Should().BeOfType<string>().Which.Should().Be("r");
            }

            return true;
        }

        public class LoginAsyncMethod
        {
            [Fact]
            public async Task ShouldEnqueueHelloAndSync()
            {
                var mockConn = new Mock<IConnection>();
                mockConn.Setup(x => x.Server).Returns(new ServerInfo(new Uri("http://neo4j.com")));
                await BoltV3.LoginAsync(mockConn.Object, "user-zhen", AuthTokens.None);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<HelloMessage>(), It.IsAny<V3.HelloResponseHandler>(), null, null),
                    Times.Once);
                mockConn.Verify(x => x.SyncAsync());
            }
        }

        public class LogoutAsyncMethod
        {
            [Fact]
            public async Task ShouldEnqueueGoodbyeAndSend()
            {
                var mockConn = new Mock<IConnection>();
                await BoltV3.LogoutAsync(mockConn.Object);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<GoodbyeMessage>(), It.IsNotNull<IResponseHandler>(), null, null),
                    Times.Once);
                mockConn.Verify(x => x.SendAsync());
            }
        }

        public class RunInAutoCommitTransactionAsyncMethod
        {
            [Fact]
            public async Task ShouldEnqueueRunAndPullAllAndSend()
            {
                var mockConn =  NewConnectionWithMode();
                var statement = new Statement("A cypher query");
                var bookmarkTracker = new Mock<IBookmarkTracker>();
                var resourceHandler = new Mock<IResultResourceHandler>();

                mockConn.Setup(x => x.EnqueueAsync(It.IsAny<RunMessage>(), It.IsAny<V3.RunResponseHandler>(),
                        It.IsAny<PullAllMessage>(), It.IsAny<V3.PullResponseHandler>()))
                    .Returns(TaskHelper.GetCompletedTask())
                    .Callback<IRequestMessage, IResponseHandler, IRequestMessage, IResponseHandler>(
                        (msg1, h1, msg2, h2) => { h1.OnSuccess(new Dictionary<string, object>()); });

                await BoltV3.RunInAutoCommitTransactionAsync(mockConn.Object, statement, true, bookmarkTracker.Object,
                    resourceHandler.Object, null, null);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<RunWithMetadataMessage>(), It.IsAny<V3.RunResponseHandler>(),
                        PullAllMessage.PullAll,
                        It.IsAny<V3.PullResponseHandler>()), Times.Once);
                mockConn.Verify(x => x.SendAsync());
            }

            [Fact]
            public async Task ResultBuilderShouldObtainServerInfoFromConnection()
            {
                var mockConn = NewConnectionWithMode();
                var statement = new Statement("A cypher query");
                var bookmarkTracker = new Mock<IBookmarkTracker>();
                var resourceHandler = new Mock<IResultResourceHandler>();

                mockConn.Setup(x => x.EnqueueAsync(It.IsAny<RunMessage>(), It.IsAny<V3.RunResponseHandler>(),
                        It.IsAny<PullAllMessage>(), It.IsAny<V3.PullResponseHandler>()))
                    .Returns(TaskHelper.GetCompletedTask())
                    .Callback<IRequestMessage, IResponseHandler, IRequestMessage, IResponseHandler>(
                        (msg1, h1, msg2, h2) => { h1.OnSuccess(new Dictionary<string, object>()); });

                await BoltV3.RunInAutoCommitTransactionAsync(mockConn.Object, statement, true, bookmarkTracker.Object,
                    resourceHandler.Object, null, null);

                mockConn.Verify(x => x.Server, Times.Once);
            }
            
            [Theory]
            [InlineData(AccessMode.Read)]
            [InlineData(AccessMode.Write)]
            public async Task ShouldPassBookmarkAndTxConfigToRunWithMetadataMessage(AccessMode mode)
            {
                var mockConn = NewConnectionWithMode(mode);
                var statement = new Statement("A cypher query");
                var bookmarkTracker = new Mock<IBookmarkTracker>();
                var resourceHandler = new Mock<IResultResourceHandler>();

                mockConn.Setup(x => x.EnqueueAsync(It.IsAny<RunMessage>(), It.IsAny<V3.RunResponseHandler>(),
                        It.IsAny<PullAllMessage>(), It.IsAny<V3.PullResponseHandler>()))
                    .Returns(TaskHelper.GetCompletedTask())
                    .Callback<IRequestMessage, IResponseHandler, IRequestMessage, IResponseHandler>(
                        (m1, h1, m2, h2) =>
                        {
                            h1.OnSuccess(new Dictionary<string, object>());
                            VerifyMetadata(m1.CastOrThrow<RunWithMetadataMessage>().Metadata, mode);
                        });

                await BoltV3.RunInAutoCommitTransactionAsync(mockConn.Object, statement, true, bookmarkTracker.Object,
                    resourceHandler.Object, Bookmark, TxConfig);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<RunWithMetadataMessage>(), It.IsAny<V3.RunResponseHandler>(),
                        PullAllMessage.PullAll,
                        It.IsAny<V3.PullResponseHandler>()),
                    Times.Once);
            }
        }

        public class BeginTransactionAsyncMethod
        {
            [Fact]
            public async Task ShouldNotSyncIfBookmarkIsNull()
            {
                var mockConn = NewConnectionWithMode();

                await BoltV3.BeginTransactionAsync(mockConn.Object, null, null);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<BeginMessage>(), It.IsAny<V1.BeginResponseHandler>(), null, null),
                    Times.Once);
                mockConn.Verify(x => x.SyncAsync(), Times.Never);
            }

            [Fact]
            public async Task ShouldNotSyncIfInvalidBookmarkGiven()
            {
                var mockConn = NewConnectionWithMode();
                var bookmark = Bookmark.From((string)null);

                await BoltV3.BeginTransactionAsync(mockConn.Object, bookmark, null);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<BeginMessage>(), It.IsAny<V1.BeginResponseHandler>(), null, null),
                    Times.Once);
                mockConn.Verify(x => x.SyncAsync(), Times.Never);
            }

            [Fact]
            public async Task ShouldSyncIfValidBookmarkGiven()
            {
                var mockConn = NewConnectionWithMode();
                var bookmark = Bookmark.From(SessionTests.FakeABookmark(234));

                await BoltV3.BeginTransactionAsync(mockConn.Object, bookmark, null);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<BeginMessage>(), It.IsAny<V1.BeginResponseHandler>(), null, null),
                    Times.Once);
                mockConn.Verify(x => x.SyncAsync(), Times.Once);
            }
            
            [Theory]
            [InlineData(AccessMode.Read)]
            [InlineData(AccessMode.Write)]
            public async Task ShouldPassBookmarkTxConfigAndModeToRunWithMetadataMessage(AccessMode mode)
            {
                var mockConn = NewConnectionWithMode(mode);

                mockConn.Setup(x =>
                        x.EnqueueAsync(It.IsAny<BeginMessage>(), It.IsAny<V1.BeginResponseHandler>(), null, null))
                    .Returns(TaskHelper.GetCompletedTask())
                    .Callback<IRequestMessage, IResponseHandler, IRequestMessage, IResponseHandler>(
                        (m1, h1, m2, h2) =>
                        {
                            var msg = m1.CastOrThrow<BeginMessage>();
                            VerifyMetadata(msg.Metadata, mode);
                        });

                await BoltV3.BeginTransactionAsync(mockConn.Object, Bookmark, TxConfig);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<BeginMessage>(), It.IsAny<V1.BeginResponseHandler>(), null, null),
                    Times.Once);
            }
        }

        public class CommitTransactionAsyncMethod
        {
            [Fact]
            public async Task EnqueueCommitAndSync()
            {
                var mockConn = NewConnectionWithMode();
                var bookmarkTracker = new Mock<IBookmarkTracker>();

                await BoltV3.CommitTransactionAsync(mockConn.Object, bookmarkTracker.Object);

                mockConn.Verify(
                    x => x.EnqueueAsync(CommitMessage.Commit, It.IsAny<V1.CommitResponseHandler>(), null, null),
                    Times.Once);
                mockConn.Verify(x => x.SyncAsync(), Times.Once);
            }
        }

        public class RollbackTransactionAsyncMethod
        {
            [Fact]
            public async Task ShouldEnqueueRollbackAndSync()
            {
                var mockConn = NewConnectionWithMode();

                await BoltV3.RollbackTransactionAsync(mockConn.Object);

                mockConn.Verify(
                    x => x.EnqueueAsync(RollbackMessage.Rollback, It.IsAny<V1.RollbackResponseHandler>(), null, null),
                    Times.Once);
                mockConn.Verify(x => x.SyncAsync(), Times.Once);
            }
        }

        public class RunInExplicitTransactionAsyncMethod
        {
            [Fact]
            public async Task ShouldRunPullAllSync()
            {
                var mockConn = SessionTests.MockedConnectionWithSuccessResponse();
                var statement = new Statement("lalala");

                await BoltV3.RunInExplicitTransactionAsync(mockConn.Object, statement, true);

                mockConn.Verify(
                    x => x.EnqueueAsync(It.IsAny<RunWithMetadataMessage>(), It.IsAny<V3.RunResponseHandler>(),
                        PullAllMessage.PullAll,
                        It.IsAny<V3.PullResponseHandler>()),
                    Times.Once);
                mockConn.Verify(x => x.SendAsync(), Times.Once);
            }

            [Fact]
            public async Task ResultBuilderShouldObtainServerInfoFromConnection()
            {
                var mockConn = SessionTests.MockedConnectionWithSuccessResponse();
                var statement = new Statement("lalala");

                await BoltV3.RunInExplicitTransactionAsync(mockConn.Object, statement, true);
                mockConn.Verify(x => x.Server, Times.Once);
            }
        }

    }
}