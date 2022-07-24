// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Quic.Tests
{
    [Collection(nameof(DisableParallelization))]
    [ConditionalClass(typeof(QuicTestBase), nameof(QuicTestBase.IsSupported))]
    public class MsQuicTests : QuicTestBase
    {
        private static byte[] s_data = "Hello world!"u8.ToArray();

        public MsQuicTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task ConnectWithCertificateChain()
        {
            (X509Certificate2 certificate, X509Certificate2Collection chain) = System.Net.Security.Tests.TestHelper.GenerateCertificates("localhost", longChain: true);
            try
            {
                X509Certificate2 rootCA = chain[chain.Count - 1];

                var listenerOptions = new QuicListenerOptions()
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                    ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                    ConnectionOptionsCallback = (_, _, _) =>
                    {
                        var serverOptions = CreateQuicServerOptions();
                        serverOptions.ServerAuthenticationOptions.ServerCertificateContext = SslStreamCertificateContext.Create(certificate, chain);
                        serverOptions.ServerAuthenticationOptions.ServerCertificate = null;
                        return ValueTask.FromResult(serverOptions);
                    }
                };

                // Use whatever endpoint, it'll get overwritten in CreateConnectedQuicConnection.
                QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(listenerOptions.ListenEndPoint);
                clientOptions.ClientAuthenticationOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    Assert.Equal(certificate.Subject, cert.Subject);
                    Assert.Equal(certificate.Issuer, cert.Issuer);

                    // Dispose of the chain's elements before calling Build, which will overwrite them.
                    for (int i = 0; i < chain.ChainElements.Count; i++)
                    {
                        chain.ChainElements[i].Certificate.Dispose();
                    }

                    // We should get full chain without root CA.
                    // With trusted root, we should be able to build chain.
                    chain.ChainPolicy.CustomTrustStore.Add(rootCA);
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    bool ret = chain.Build(certificate);
                    if (!ret)
                    {
                        _output.WriteLine("Chain build failed with {0} elements", chain.ChainElements);
                        foreach (X509ChainElement element in chain.ChainElements)
                        {
                            _output.WriteLine("Element subject {0} and issuer {1}", element.Certificate.Subject, element.Certificate.Issuer);
                            _output.WriteLine("Element status len {0}", element.ChainElementStatus.Length);
                            foreach (X509ChainStatus status in element.ChainElementStatus)
                            {
                                _output.WriteLine($"Status:  {status.Status}: {status.StatusInformation}");
                            }
                        }
                    }

                    return ret;
                };

                (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(clientOptions, listenerOptions);
                using X509Certificate clientRemoteCertificate = clientConnection.RemoteCertificate;
                Assert.Equal(certificate, clientRemoteCertificate);
                Assert.Null(serverConnection.RemoteCertificate);
                await serverConnection.DisposeAsync();
                await clientConnection.DisposeAsync();
            }
            finally
            {
                foreach (X509Certificate2 cert in chain)
                {
                    cert.Dispose();
                }
                certificate.Dispose();
            }
        }

        [ConditionalFact]
        public async Task UntrustedClientCertificateFails()
        {
            if (PlatformDetection.IsWindows10Version20348OrLower)
            {
                throw new SkipTestException("Client certificates are not supported on Windows Server 2022.");
            }

            var listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverOptions = CreateQuicServerOptions();
                    serverOptions.ServerAuthenticationOptions.ClientCertificateRequired = true;
                    serverOptions.ServerAuthenticationOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        return false;
                    };
                    return ValueTask.FromResult(serverOptions);
                }
            };

            await using QuicListener listener = await CreateQuicListener(listenerOptions);
            QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(listener.LocalEndPoint);
            clientOptions.ClientAuthenticationOptions.ClientCertificates = new X509CertificateCollection() { ClientCertificate };
            Task<QuicConnection> clientTask = CreateQuicConnection(clientOptions).AsTask();

            // This will propagate the AuthenticationException since the client certificate is not trusted.
            Task<QuicConnection> serverTask = listener.AcceptConnectionAsync().AsTask();

            Assert.True(clientTask.Wait(PassingTestTimeout));
            await Assert.ThrowsAsync<AuthenticationException>(() => serverTask);
            // The task will likely succeed but we don't really care.
            // It may fail if the server aborts quickly.
            try
            {
                await using QuicConnection clientConnection = await clientTask;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Ignored exception:{Environment.NewLine}{ex}");
            }
        }

        [Fact]
        public async Task CertificateCallbackThrowPropagates()
        {
            using CancellationTokenSource cts = new CancellationTokenSource(PassingTestTimeout);
            bool validationResult = false;

            var listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(Socket.OSSupportsIPv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(CreateQuicServerOptions())
            };
            await using QuicListener listener = await CreateQuicListener(listenerOptions);

            QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(listener.LocalEndPoint);
            clientOptions.ClientAuthenticationOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                Assert.Equal(ServerCertificate, cert);
                if (validationResult)
                {
                    return validationResult;
                }

                throw new ArithmeticException("foobar");
            };

            clientOptions.ClientAuthenticationOptions.TargetHost = "foobar1";

            await Assert.ThrowsAsync<ArithmeticException>(() => CreateQuicConnection(clientOptions).AsTask());
            await Assert.ThrowsAsync<AuthenticationException>(async () => await listener.AcceptConnectionAsync());

            // Make sure the listener is still usable and there is no lingering bad connection
            validationResult = true;
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(listener);
            await PingPong(clientConnection, serverConnection);
            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Fact]
        public async Task ConnectWithServerCertificateCallback()
        {
            using X509Certificate2 c1 = System.Net.Test.Common.Configuration.Certificates.GetServerCertificate();
            using X509Certificate2 c2 = System.Net.Test.Common.Configuration.Certificates.GetClientCertificate(); // This 'wrong' certificate but should be sufficient
            X509Certificate2 expectedCertificate = c1;

            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(PassingTestTimeout);
            string? receivedHostName = null;
            X509Certificate? receivedCertificate = null;

            var listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(Socket.OSSupportsIPv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverOptions = CreateQuicServerOptions();
                    serverOptions.ServerAuthenticationOptions.ServerCertificate = null;
                    serverOptions.ServerAuthenticationOptions.ServerCertificateSelectionCallback = (sender, hostName) =>
                    {
                        receivedHostName = hostName;
                        if (hostName == "foobar1")
                        {
                            return c1;
                        }
                        else if (hostName == "foobar2")
                        {
                            return c2;
                        }

                        return null;
                    };
                    return ValueTask.FromResult(serverOptions);
                }
            };

            await using QuicListener listener = await CreateQuicListener(listenerOptions);
            QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(listener.LocalEndPoint);
            clientOptions.ClientAuthenticationOptions.TargetHost = "foobar1";
            clientOptions.ClientAuthenticationOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                receivedCertificate = cert;
                return true;
            };

            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(clientOptions, listener);
            Assert.Equal(clientOptions.ClientAuthenticationOptions.TargetHost, receivedHostName);
            Assert.Equal(c1, receivedCertificate);
            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();

            // This should fail when callback return null.
            clientOptions.ClientAuthenticationOptions.TargetHost = "foobar3";
            Task clientTask = CreateQuicConnection(clientOptions).AsTask();

            await Assert.ThrowsAsync<QuicException>(() => clientTask);
            Assert.Equal(clientOptions.ClientAuthenticationOptions.TargetHost, receivedHostName);
            await Assert.ThrowsAsync<ArgumentException>(async () => await listener.AcceptConnectionAsync());

            // Do this last to make sure Listener is still functional.
            clientOptions.ClientAuthenticationOptions.TargetHost = "foobar2";
            expectedCertificate = c2;

            (clientConnection, serverConnection) = await CreateConnectedQuicConnection(clientOptions, listener);
            Assert.Equal(clientOptions.ClientAuthenticationOptions.TargetHost, receivedHostName);
            Assert.Equal(c2, receivedCertificate);
            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("localhost")]
        public async Task ConnectWithIpSetsSni(string destination)
        {
            using X509Certificate2 certificate = System.Net.Test.Common.Configuration.Certificates.GetServerCertificate();
            string expectedName = "foobar";
            string? receivedHostName = null;

            var listenerOptions = new QuicListenerOptions()
            {
                // loopback may resolve to IPv6
                ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverOptions = CreateQuicServerOptions();
                    serverOptions.ServerAuthenticationOptions.ServerCertificate = null;
                    serverOptions.ServerAuthenticationOptions.ServerCertificateSelectionCallback = (sender, hostName) =>
                    {
                        receivedHostName = hostName;
                        return certificate;
                    };
                    return ValueTask.FromResult(serverOptions);
                }
            };

            await using QuicListener listener = await CreateQuicListener(listenerOptions);

            QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(new DnsEndPoint(destination, listener.LocalEndPoint.Port));
            clientOptions.ClientAuthenticationOptions.TargetHost = expectedName;

            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(clientOptions, listener);
            Assert.Equal(expectedName, receivedHostName);
            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Fact]
        public async Task ConnectWithCertificateForDifferentName_Throws()
        {
            (X509Certificate2 certificate, X509Certificate2Collection chain) = System.Net.Security.Tests.TestHelper.GenerateCertificates("localhost");
            try
            {
                var quicOptions = new QuicListenerOptions()
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                    ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                    ConnectionOptionsCallback = (_, _, _) =>
                    {
                        var serverOptions = CreateQuicServerOptions();
                        serverOptions.ServerAuthenticationOptions.ServerCertificate = certificate;
                        return ValueTask.FromResult(serverOptions);
                    }
                };
                await using QuicListener listener = await CreateQuicListener(quicOptions);

                QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(listener.LocalEndPoint);
                // Use different target host on purpose to get RemoteCertificateNameMismatch ssl error.
                clientOptions.ClientAuthenticationOptions.TargetHost = "loopback";
                clientOptions.ClientAuthenticationOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    Assert.Equal(certificate.Subject, cert.Subject);
                    Assert.Equal(certificate.Issuer, cert.Issuer);
                    Assert.Equal(SslPolicyErrors.RemoteCertificateNameMismatch, errors & SslPolicyErrors.RemoteCertificateNameMismatch);
                    return SslPolicyErrors.None == errors;
                };

                await Assert.ThrowsAsync<AuthenticationException>(async () => await CreateQuicConnection(clientOptions));
            }
            finally
            {
                foreach (X509Certificate2 cert in chain)
                {
                    cert.Dispose();
                }
                certificate.Dispose();
            }
        }

        [ConditionalTheory]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("127.0.0.1", false)]
        [InlineData("::1", false)]
        public async Task ConnectWithCertificateForLoopbackIP_IndicatesExpectedError(string ipString, bool expectsError)
        {
            var ipAddress = IPAddress.Parse(ipString);

            (X509Certificate2 certificate, X509Certificate2Collection chain) = System.Net.Security.Tests.TestHelper.GenerateCertificates(expectsError ? "badhost" : "localhost");
            try
            {
                var listenerOptions = new QuicListenerOptions()
                {
                    ListenEndPoint = new IPEndPoint(ipAddress, 0),
                    ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                    ConnectionOptionsCallback = (_, _, _) =>
                    {
                        var serverOptions = CreateQuicServerOptions();
                        serverOptions.ServerAuthenticationOptions.ServerCertificate = certificate;
                        return ValueTask.FromResult(serverOptions);
                    }
                };

                // Use whatever endpoint, it'll get overwritten in CreateConnectedQuicConnection.
                QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(listenerOptions.ListenEndPoint);
                clientOptions.ClientAuthenticationOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    Assert.Equal(certificate.Subject, cert.Subject);
                    Assert.Equal(certificate.Issuer, cert.Issuer);
                    Assert.Equal(expectsError ? SslPolicyErrors.RemoteCertificateNameMismatch : SslPolicyErrors.None, errors & SslPolicyErrors.RemoteCertificateNameMismatch);
                    return true;
                };

                (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(clientOptions, listenerOptions);
                await clientConnection.DisposeAsync();
                await serverConnection.DisposeAsync();
            }
            finally
            {
                foreach (X509Certificate2 cert in chain)
                {
                    cert.Dispose();
                }
                certificate.Dispose();
            }
        }

        [ConditionalTheory]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/64944", TestPlatforms.Windows)]
        public async Task ConnectWithClientCertificate(bool sendCertificate, bool useClientSelectionCallback)
        {
            if (PlatformDetection.IsWindows10Version20348OrLower)
            {
                throw new SkipTestException("Client certificates are not supported on Windows Server 2022.");
            }

            bool clientCertificateOK = false;

            var listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverOptions = CreateQuicServerOptions();
                    serverOptions.ServerAuthenticationOptions.ClientCertificateRequired = true;
                    serverOptions.ServerAuthenticationOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        if (sendCertificate)
                        {
                            _output.WriteLine("client certificate {0}", cert);
                            Assert.NotNull(cert);
                            Assert.Equal(ClientCertificate.Thumbprint, ((X509Certificate2)cert).Thumbprint);
                        }

                        clientCertificateOK = true;
                        return true;
                    };
                    return ValueTask.FromResult(serverOptions);
                }
            };

            await using QuicListener listener = await CreateQuicListener(listenerOptions);
            QuicClientConnectionOptions clientOptions = CreateQuicClientOptions(listener.LocalEndPoint);
            if (useClientSelectionCallback)
            {
                clientOptions.ClientAuthenticationOptions.LocalCertificateSelectionCallback = delegate
                {
                    return sendCertificate ? ClientCertificate : null;
                };
            }
            else if (sendCertificate)
            {
                clientOptions.ClientAuthenticationOptions.ClientCertificates = new X509CertificateCollection() { ClientCertificate };
            }
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(clientOptions, listener);

            // Verify functionality of the connections.
            await PingPong(clientConnection, serverConnection);
            // check we completed the client certificate verification.
            Assert.True(clientCertificateOK);
            using X509Certificate serverRemoteCertificate = serverConnection.RemoteCertificate;
            Assert.Equal(sendCertificate ? ClientCertificate : null, serverRemoteCertificate);

            await serverConnection.CloseAsync(0);
            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpenStreamAsync_BlocksUntilAvailable(bool unidirectional)
        {
            ValueTask<QuicStream> OpenStreamAsync(QuicConnection connection) => unidirectional
                ? connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional)
                : connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);


            QuicListenerOptions listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverOptions = CreateQuicServerOptions();
                    serverOptions.MaxInboundBidirectionalStreams = 1;
                    serverOptions.MaxInboundUnidirectionalStreams = 1;
                    return ValueTask.FromResult(serverOptions);
                }
            };
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(null, listenerOptions);

            // Open one stream, second call should block
            QuicStream stream = await OpenStreamAsync(clientConnection);
            ValueTask<QuicStream> waitTask = OpenStreamAsync(clientConnection);
            Assert.False(waitTask.IsCompleted);

            // Close the streams, the waitTask should finish as a result.
            await stream.DisposeAsync();
            QuicStream newStream = await serverConnection.AcceptInboundStreamAsync();
            await newStream.DisposeAsync();

            newStream = await waitTask.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await newStream.DisposeAsync();

            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpenStreamAsync_Canceled_Throws_OperationCanceledException(bool unidirectional)
        {
            ValueTask<QuicStream> OpenStreamAsync(QuicConnection connection, CancellationToken token = default) => unidirectional
                ? connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token)
                : connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);

            QuicListenerOptions listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverOptions = CreateQuicServerOptions();
                    serverOptions.MaxInboundBidirectionalStreams = 1;
                    serverOptions.MaxInboundUnidirectionalStreams = 1;
                    return ValueTask.FromResult(serverOptions);
                }
            };
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(null, listenerOptions);

            CancellationTokenSource cts = new CancellationTokenSource();

            // Open one stream, second call should block
            QuicStream stream = await OpenStreamAsync(clientConnection);
            ValueTask<QuicStream> waitTask = OpenStreamAsync(clientConnection, cts.Token);
            Assert.False(waitTask.IsCompleted);

            cts.Cancel();

            // awaiting the task should throw
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask.AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(cts.Token, ex.CancellationToken);

            // Close the streams, the waitTask should finish as a result.
            await stream.DisposeAsync();
            QuicStream newStream = await serverConnection.AcceptInboundStreamAsync();
            await newStream.DisposeAsync();

            // next call should work as intended
            newStream = await OpenStreamAsync(clientConnection).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await newStream.DisposeAsync();

            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpenStreamAsync_PreCanceled_Throws_OperationCanceledException(bool unidirectional)
        {
            ValueTask<QuicStream> OpenStreamAsync(QuicConnection connection, CancellationToken token = default) => unidirectional
                ? connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token)
                : connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);

            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(null, CreateQuicListenerOptions());

            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenStreamAsync(clientConnection, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(cts.Token, ex.CancellationToken);

            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)] // the code path for uni/bidirectional streams differs only in a flag passed to MsQuic, so there is no need to test all possible combinations.
        public async Task OpenStreamAsync_ConnectionAbort_Throws(bool unidirectional, bool localAbort)
        {
            ValueTask<QuicStream> OpenStreamAsync(QuicConnection connection, CancellationToken token = default) => unidirectional
                ? connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token)
                : connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);

            QuicListenerOptions listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverOptions = CreateQuicServerOptions();
                    serverOptions.MaxInboundBidirectionalStreams = 1;
                    serverOptions.MaxInboundUnidirectionalStreams = 1;
                    return ValueTask.FromResult(serverOptions);
                }
            };
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(null, listenerOptions);

            // Open one stream, second call should block
            using QuicStream stream = await OpenStreamAsync(clientConnection);
            ValueTask<QuicStream> waitTask = OpenStreamAsync(clientConnection);
            Assert.False(waitTask.IsCompleted);

            if (localAbort)
            {
                await clientConnection.CloseAsync(0);
                // TODO: This may not always throw QuicOperationAbortedException due to a data race with MsQuic worker threads
                // (CloseAsync may be processed before OpenStreamAsync as it is scheduled to the front of the operation queue)
                // To be revisited once we standartize on exceptions.
                // [ActiveIssue("https://github.com/dotnet/runtime/issues/55619")]
                await Assert.ThrowsAsync<QuicException>(() => waitTask.AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            }
            else
            {
                await serverConnection.CloseAsync(0);
                await AssertThrowsQuicExceptionAsync(QuicError.ConnectionAborted, () => waitTask.AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            }

            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }


        [Fact]
        [OuterLoop("May take several seconds")]
        public async Task SetListenerTimeoutWorksWithSmallTimeout()
        {
            var listenerOptions = new QuicListenerOptions()
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new List<SslApplicationProtocol>() { ApplicationProtocol },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(CreateQuicServerOptions())
            };

            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection(null, listenerOptions);
            await AssertThrowsQuicExceptionAsync(QuicError.ConnectionIdle, async () => await serverConnection.AcceptInboundStreamAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(100)));
            await serverConnection.DisposeAsync();
            await clientConnection.DisposeAsync();
        }

        [Theory]
        [MemberData(nameof(WriteData))]
        public async Task WriteTests(int[][] writes)
        {
            await RunClientServer(
                async clientConnection =>
                {
                    await using QuicStream stream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional);

                    foreach (int[] bufferLengths in writes)
                    {
                        foreach (int bufferLength in bufferLengths)
                        {
                            await stream.WriteAsync(new byte[bufferLength]);
                        }
                    }

                    stream.CompleteWrites();
                },
                async serverConnection =>
                {
                    await using QuicStream stream = await serverConnection.AcceptInboundStreamAsync();

                    var buffer = new byte[4096];
                    int receivedBytes = 0, totalBytes = 0;

                    while ((receivedBytes = await stream.ReadAsync(buffer)) != 0)
                    {
                        totalBytes += receivedBytes;
                    }

                    int expectedTotalBytes = writes.SelectMany(x => x).Sum();
                    Assert.Equal(expectedTotalBytes, totalBytes);

                    stream.CompleteWrites();
                });
        }

        public static IEnumerable<object[]> WriteData()
        {
            var bufferSizes = new[] { 1, 502, 15_003, 1_000_004 };
            var r = new Random();

            return
                from bufferCount in new[] { 1, 2, 3, 10 }
                let writes =
                    Enumerable.Range(0, 5)
                    .Select(_ =>
                        Enumerable.Range(0, bufferCount)
                        .Select(_ => bufferSizes[r.Next(bufferSizes.Length)])
                        .ToArray())
                    .ToArray()
                select new object[] { writes };
        }

        [Fact]
        public async Task CallDifferentWriteMethodsWorks()
        {
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();

            ReadOnlyMemory<byte> helloWorld = "Hello world!"u8.ToArray();

            await using QuicStream clientStream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
            ValueTask writeTask = clientStream.WriteAsync(helloWorld, completeWrites: true);
            await using QuicStream serverStream = await serverConnection.AcceptInboundStreamAsync();

            await writeTask;
            byte[] memory = new byte[24];
            int res = await serverStream.ReadAsync(memory);
            Assert.Equal(12, res);

            await clientConnection.DisposeAsync();
            await serverConnection.DisposeAsync();
        }

        [Fact]
        public async Task CloseAsync_ByServer_AcceptThrows()
        {
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();

            await using (clientConnection)
            await using (serverConnection)
            {
                var acceptTask = serverConnection.AcceptInboundStreamAsync();
                await serverConnection.CloseAsync(errorCode: 0);
                // make sure we throw
                await AssertThrowsQuicExceptionAsync(QuicError.OperationAborted, () => acceptTask.AsTask());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CloseAsync_MultipleCalls_FollowingCallsAreIgnored(bool client)
        {

            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();

            await using (clientConnection)
            await using (serverConnection)
            {
                if (client)
                {
                    await clientConnection.CloseAsync(0);
                    await clientConnection.CloseAsync(0);
                }
                else
                {
                    await serverConnection.CloseAsync(0);
                    await serverConnection.CloseAsync(0);
                }
            }
        }

        internal static ReadOnlySequence<byte> CreateReadOnlySequenceFromBytes(byte[] data)
        {
            List<byte[]> segments = new List<byte[]>
            {
                Array.Empty<byte>()
            };

            foreach (var b in data)
            {
                segments.Add(new[] { b });
                segments.Add(Array.Empty<byte>());
            }

            return CreateSegments(segments.ToArray());
        }

        private static ReadOnlySequence<byte> CreateSegments(params byte[][] inputs)
        {
            if (inputs == null || inputs.Length == 0)
            {
                throw new InvalidOperationException();
            }

            int i = 0;

            BufferSegment last = null;
            BufferSegment first = null;

            do
            {
                byte[] s = inputs[i];
                int length = s.Length;
                int dataOffset = length;
                var chars = new byte[length * 2];

                for (int j = 0; j < length; j++)
                {
                    chars[dataOffset + j] = s[j];
                }

                // Create a segment that has offset relative to the OwnedMemory and OwnedMemory itself has offset relative to array
                var memory = new Memory<byte>(chars).Slice(length, length);

                if (first == null)
                {
                    first = new BufferSegment(memory);
                    last = first;
                }
                else
                {
                    last = last.Append(memory);
                }
                i++;
            } while (i < inputs.Length);

            return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        internal class BufferSegment : ReadOnlySequenceSegment<byte>
        {
            public BufferSegment(ReadOnlyMemory<byte> memory)
            {
                Memory = memory;
            }

            public BufferSegment Append(ReadOnlyMemory<byte> memory)
            {
                var segment = new BufferSegment(memory)
                {
                    RunningIndex = RunningIndex + Memory.Length
                };
                Next = segment;
                return segment;
            }
        }

        [Fact]
        [OuterLoop("May take several seconds")]
        public async Task ByteMixingOrNativeAVE_MinimalFailingTest()
        {
            const int writeSize = 64 * 1024;
            const int NumberOfWrites = 512;
            byte[] data1 = new byte[writeSize * NumberOfWrites];
            byte[] data2 = new byte[writeSize * NumberOfWrites];
            Array.Fill(data1, (byte)1);
            Array.Fill(data2, (byte)2);

            Task t1 = RunTest(data1);
            Task t2 = RunTest(data2);

            async Task RunTest(byte[] data)
            {
                await RunClientServer(
                    iterations: 20,
                    serverFunction: async connection =>
                    {
                        await using QuicStream stream = await connection.AcceptInboundStreamAsync();

                        byte[] buffer = new byte[data.Length];
                        int bytesRead = await ReadAll(stream, buffer);
                        Assert.Equal(data.Length, bytesRead);
                        AssertExtensions.SequenceEqual(data, buffer);

                        for (int pos = 0; pos < data.Length; pos += writeSize)
                        {
                            await stream.WriteAsync(data[pos..(pos + writeSize)]);
                        }
                        await stream.WriteAsync(Memory<byte>.Empty, completeWrites: true);
                    },
                    clientFunction: async connection =>
                    {
                        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);

                        for (int pos = 0; pos < data.Length; pos += writeSize)
                        {
                            await stream.WriteAsync(data[pos..(pos + writeSize)]);
                        }
                        await stream.WriteAsync(Memory<byte>.Empty, completeWrites: true);

                        byte[] buffer = new byte[data.Length];
                        int bytesRead = await ReadAll(stream, buffer);
                        Assert.Equal(data.Length, bytesRead);
                        AssertExtensions.SequenceEqual(data, buffer);
                    }
                );
            }

            await (new[] { t1, t2 }).WhenAllOrAnyFailed(millisecondsTimeout: 1000000);
        }

        [Fact]
        public async Task ManagedAVE_MinimalFailingTest()
        {
            async Task GetStreamIdWithoutStartWorks()
            {
                (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();

                await using QuicStream clientStream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                Assert.Equal(0, clientStream.Id);

                // TODO: stream that is opened by client but left unaccepted by server may cause AccessViolationException in its Finalizer
                await clientConnection.DisposeAsync();
                await serverConnection.DisposeAsync();
            }

            await GetStreamIdWithoutStartWorks().WaitAsync(TimeSpan.FromSeconds(15));

            GC.Collect();
        }

        [Fact]
        public async Task DisposingConnection_OK()
        {
            async Task GetStreamIdWithoutStartWorks()
            {
                (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();

                await using QuicStream clientStream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                Assert.Equal(0, clientStream.Id);

                // Dispose all connections before the streams;
                await clientConnection.DisposeAsync();
                await serverConnection.DisposeAsync();
            }

            await GetStreamIdWithoutStartWorks();

            GC.Collect();
        }

        [Fact]
        public async Task Read_ConnectionAbortedByPeer_Throws()
        {
            const int ExpectedErrorCode = 1234;

            await Task.Run(async () =>
            {
                (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();

                await using QuicStream clientStream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                await clientStream.WriteAsync(new byte[1]);

                await using QuicStream serverStream = await serverConnection.AcceptInboundStreamAsync();
                await serverStream.ReadAsync(new byte[1]);

                await clientConnection.CloseAsync(ExpectedErrorCode);

                byte[] buffer = new byte[100];
                await AssertThrowsQuicExceptionAsync(QuicError.OperationAborted, () => clientStream.ReadAsync(buffer).AsTask());
                QuicException ex = await AssertThrowsQuicExceptionAsync(QuicError.ConnectionAborted, () => serverStream.ReadAsync(buffer).AsTask());
                Assert.Equal(ExpectedErrorCode, ex.ApplicationErrorCode);

                await serverConnection.DisposeAsync();
                await clientConnection.DisposeAsync();
            }).WaitAsync(TimeSpan.FromMilliseconds(PassingTestTimeoutMilliseconds));
        }

        [Fact]
        public async Task Read_ConnectionAbortedByUser_Throws()
        {
            const int ExpectedErrorCode = 1234;

            await Task.Run(async () =>
            {
                (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();

                await using QuicStream clientStream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                await clientStream.WriteAsync(new byte[1]);

                await using QuicStream serverStream = await serverConnection.AcceptInboundStreamAsync();
                await serverStream.ReadAsync(new byte[1]);

                await serverConnection.CloseAsync(ExpectedErrorCode);

                byte[] buffer = new byte[100];
                QuicException ex = await AssertThrowsQuicExceptionAsync(QuicError.ConnectionAborted, () => clientStream.ReadAsync(buffer).AsTask());
                Assert.Equal(ExpectedErrorCode, ex.ApplicationErrorCode);
                await AssertThrowsQuicExceptionAsync(QuicError.OperationAborted, () => serverStream.ReadAsync(buffer).AsTask());

                await serverConnection.DisposeAsync();
                await clientConnection.DisposeAsync();
            }).WaitAsync(TimeSpan.FromMilliseconds(PassingTestTimeoutMilliseconds));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task BigWrite_SmallRead_Success(bool closeWithData)
        {
            const int size = 100;
            (QuicConnection clientConnection, QuicConnection serverConnection) = await CreateConnectedQuicConnection();
            await using (clientConnection)
            await using (serverConnection)
            {
                byte[] buffer = new byte[1] { 42 };

                QuicStream clientStream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                Task<QuicStream> t = serverConnection.AcceptInboundStreamAsync().AsTask();
                await TaskTimeoutExtensions.WhenAllOrAnyFailed(clientStream.WriteAsync(buffer).AsTask(), t, PassingTestTimeoutMilliseconds);
                QuicStream serverStream = t.Result;
                Assert.Equal(1, await serverStream.ReadAsync(buffer));

                // streams are new established and in good shape.
                using (clientStream)
                using (serverStream)
                {
                    byte[] expected = RandomNumberGenerator.GetBytes(size);
                    byte[] actual = new byte[size];

                    // should be small enough to fit.
                    await serverStream.WriteAsync(expected, closeWithData);

                    // Add delay to have chance to receive the 100b block before ReadAsync starts.
                    await Task.Delay(10);
                    int remaining = size;
                    int readLength;
                    while (remaining > 0)
                    {
                        readLength = await clientStream.ReadAsync(new Memory<byte>(actual, size - remaining, 1));
                        Assert.Equal(1, readLength);
                        remaining--;
                    }

                    Assert.Equal(expected, actual);

                    if (!closeWithData)
                    {
                        serverStream.CompleteWrites();
                    }

                    readLength = await clientStream.ReadAsync(actual);
                    Assert.Equal(0, readLength);

                    Assert.Equal(expected, actual);
                }
            }
        }

        [Fact]
        public async Task BasicTest_WithReadsCompletedCheck()
        {
            await RunClientServer(
                iterations: 100,
                serverFunction: async connection =>
                {
                    await using QuicStream stream = await connection.AcceptInboundStreamAsync();
                    Assert.False(stream.ReadsClosed.IsCompleted);

                    byte[] buffer = new byte[s_data.Length];
                    int bytesRead = await ReadAll(stream, buffer);

                    Assert.True(stream.ReadsClosed.IsCompletedSuccessfully);
                    Assert.Equal(s_data.Length, bytesRead);
                    Assert.Equal(s_data, buffer);

                    await stream.WriteAsync(s_data, completeWrites: true);
                },
                clientFunction: async connection =>
                {
                    await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                    Assert.False(stream.ReadsClosed.IsCompleted);

                    await stream.WriteAsync(s_data, completeWrites: true);

                    byte[] buffer = new byte[s_data.Length];
                    int bytesRead = await ReadAll(stream, buffer);

                    Assert.True(stream.ReadsClosed.IsCompletedSuccessfully);
                    Assert.Equal(s_data.Length, bytesRead);
                    Assert.Equal(s_data, buffer);
                }
            );
        }

        [Fact]
        public async Task Read_ReadsCompleted_ReportedBeforeReturning0()
        {
            await RunBidirectionalClientServer(
                async clientStream =>
                {
                    await clientStream.WriteAsync(new byte[1], completeWrites: true);
                },
                async serverStream =>
                {
                    Assert.False(serverStream.ReadsClosed.IsCompleted);

                    var received = await serverStream.ReadAsync(new byte[1]);
                    Assert.Equal(1, received);
                    Assert.True(serverStream.ReadsClosed.IsCompletedSuccessfully);

                    var task = serverStream.ReadAsync(new byte[1]);
                    Assert.True(task.IsCompleted);

                    received = await task;
                    Assert.Equal(0, received);
                    Assert.True(serverStream.ReadsClosed.IsCompletedSuccessfully);
                });
        }
    }
}
