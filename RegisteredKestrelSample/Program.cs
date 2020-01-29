using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NclLab.Kestrel;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace RegisteredKestrelSample
{
    public class Program
    {
        const string ListenRoute = "/google.pubsub.v2.PublisherService/CreateTopic";
        const string ListenHost = "localhost";
        const int ListenPort = 54321;

        Uri _endpointUri;
        HttpClient _client;
        IWebHost _webHost;

        [GlobalSetup]
        public void Setup()
        {
            _endpointUri = new Uri(new Uri($"https://{ListenHost}:{ListenPort}/"), ListenRoute);
            _client = CreateHttpClient();
            _webHost = CreateWebHost();
            _webHost.Start();
        }

        sealed class IdentityOptions<T> : IOptions<T> where T : class, new()
        {
            public T Value { get; }
            public IdentityOptions(T value) { Value = value; }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _client.Dispose();
            _webHost.Dispose();
        }

        [Benchmark]
        public async Task GetSimple()
        {
            using HttpRequestMessage req = new HttpRequestMessage();

            req.Method = HttpMethod.Post;
            req.RequestUri = _endpointUri;
            req.Content = new StringContent("asdf", Encoding.ASCII, "application/grpc+proto");

            using HttpResponseMessage res = await _client.SendAsync(req).ConfigureAwait(false);
        }

        static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler();
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            };

            var client = new HttpClient(handler);

            client.DefaultRequestHeaders.TryAddWithoutValidation("grpc-timeout", "15");
            client.DefaultRequestHeaders.TryAddWithoutValidation("grpc-encoding", "gzip");
            client.DefaultRequestHeaders.TryAddWithoutValidation("authorization", "Bearer y235.wef315yfh138vh31hv93hv8h3v");

            return client;
        }

        static IWebHost CreateWebHost()
        {
            return
                WebHost.CreateDefaultBuilder()
                .UseSetting("preventHostingStartup", "true")
                .UseKestrel(ko =>
                {
                    ko.Listen(IPAddress.Loopback, ListenPort, listenOptions =>
                    {
                        listenOptions.UseHttps(CreateSelfSignedCert());
                    });
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConnectionListenerFactory, RegisteredSocketTransportFactory>();
                })
                .ConfigureLogging(logging =>
                {
                    //logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                    logging.AddFilter("Microsoft.AspNetCore", LogLevel.Trace);
                })
                .Configure(app =>
                {
                    app.UseRouting().UseEndpoints(routes =>
                    {
                        routes.MapPost(ListenRoute, async ctx =>
                        {
                            await ctx.Response.WriteAsync("ok").ConfigureAwait(false);
                        });
                    });
                })
                .Build();
        }

        static X509Certificate2 CreateSelfSignedCert()
        {
            // Create self-signed cert for server.
            using RSA rsa = RSA.Create();
            var certReq = new CertificateRequest($"CN={ListenHost}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            certReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            X509Certificate2 cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
            }
            return cert;
        }

        public static void Main(string[] args)
        {
            Program p = new Program();

            p.Setup();
            p.GetSimple().GetAwaiter().GetResult();
            //PoorMansBenchmark(() => p.GetSimple().Wait());
            p.Cleanup();

            //BenchmarkRunner.Run<Program>();
        }

        static void PoorMansBenchmark(Action action)
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            Stopwatch sw = Stopwatch.StartNew();

            // Warmup.
            do
            {
                action();
            }
            while (sw.ElapsedMilliseconds < 1000);

            // Get number of loops to execute, targeting ~1s of execution for each run.
            int loops = 0;
            do
            {
                ++loops;
                action();
            }
            while (sw.ElapsedMilliseconds < 2000);

            Console.WriteLine($"Using {loops:N0} loops");

            // Keep running until we get 10 runs with no observed speed improvement.
            // Prints + when an improvement is seen, otherwise -
            long bestTicks = long.MaxValue;
            int runCount = 0;

            while (runCount < 10)
            {
                long start = Stopwatch.GetTimestamp();

                for (int i = 0; i < loops; ++i)
                {
                    action();
                }

                long end = Stopwatch.GetTimestamp();
                long curTicks = end - start;

                if (curTicks < bestTicks)
                {
                    bestTicks = curTicks;
                    runCount = 0;
                    Console.Write('+');
                }
                else
                {
                    ++runCount;
                    Console.Write('-');
                }
            }

            // Report out our best time.
            double opsPerSecond = (loops * Stopwatch.Frequency) / (double)bestTicks;
            double opTime = 1.0 / opsPerSecond;
            string timeUnit = "s";

            if (opTime < 1.0)
            {
                opTime *= 1000.0;
                timeUnit = "ms";

                if (opTime < 10)
                {
                    opTime *= 1000.0;
                    timeUnit = "us";
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Speed: {opTime:N1} {timeUnit} ({opsPerSecond:N1} reqs/s)");
        }
    }
}
