using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NclLab.Kestrel;
using System;
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

        [Params(100, 1000)]
        public int Concurrency;

        [Params(100000)]
        public int RequestCount;

        [Params(false, true)]
        public bool Registered;

        [GlobalSetup]
        public void Setup()
        {
            _endpointUri = new Uri(new Uri($"http://{ListenHost}:{ListenPort}/"), ListenRoute);
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
        public async Task Post()
        {
            Task[] tasks = new Task[RequestCount];

            for (int i = 0; i < RequestCount; ++i)
            {
                tasks[i] = PostOne();
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        async Task PostOne()
        {
            using var req = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = _endpointUri,
                Content = new StringContent("asdf", Encoding.ASCII, "application/grpc+proto")
            };

            using HttpResponseMessage res = await _client.SendAsync(req).ConfigureAwait(false);
            await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler();
            handler.MaxConnectionsPerServer = Concurrency;
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

        IWebHost CreateWebHost()
        {
            return
                WebHost.CreateDefaultBuilder()
                .UseSetting("preventHostingStartup", "true")
                .UseKestrel(ko =>
                {
                    ko.Listen(Dns.GetHostAddresses(ListenHost)[0], ListenPort, listenOptions =>
                    {
                        //listenOptions.UseHttps(CreateSelfSignedCert());
                    });
                })
                .ConfigureServices(services =>
                {
                    if(Registered) services.AddSingleton<IConnectionListenerFactory, RegisteredSocketTransportFactory>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                    //logging.AddFilter("Microsoft.AspNetCore", LogLevel.Trace);
                })
                .Configure(app =>
                {
                    app.UseRouting().UseEndpoints(routes =>
                    {
                        routes.MapPost(ListenRoute, async ctx =>
                        {
                            await ctx.Response.WriteAsync("Hello, World.").ConfigureAwait(false);
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
            var p = new Program();

            //p.Registered = true;
            //p.RequestCount = 100;
            //p.Concurrency = 1;
            //p.Setup();
            //p.Post().GetAwaiter().GetResult();
            //p.Cleanup();

            BenchmarkRunner.Run<Program>();
        }
    }
}
