﻿using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MLS.Agent.Tools;
using MLS.Repositories;
using Pocket;
using Pocket.For.ApplicationInsights;
using Recipes;
using Serilog.Sinks.RollingFileAlternate;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using WorkspaceServer;
using WorkspaceServer.Servers.Roslyn;
using static Pocket.Logger<MLS.Agent.Program>;
using SerilogLoggerConfiguration = Serilog.LoggerConfiguration;

namespace MLS.Agent
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var parser = CommandLineParser.Create(
                start: (options, console) => ConstructWebHost(options).Run(),
                tryGithub: (repo, console) => GithubHandler.Handler(repo, console, new GithubRepoLocator()));

            return await parser.InvokeAsync(args);
        }

        public static X509Certificate2 ParseKey(string base64EncodedKey)
        {
            var bytes = Convert.FromBase64String(base64EncodedKey);
            return new X509Certificate2(bytes);
        }

        private static readonly Assembly[] assembliesEmittingPocketLoggerLogs = {
            typeof(Startup).Assembly,
            typeof(AsyncLazy<>).Assembly,
            typeof(RoslynWorkspaceServer).Assembly
        };

        private static void StartLogging(CompositeDisposable disposables, StartupOptions options)
        {
            if (options.Production)
            {
                var applicationVersion = VersionSensor.Version().AssemblyInformationalVersion;
                var websiteSiteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "UNKNOWN-AGENT";
                var regionId = options.RegionId ?? "undefined";
                disposables.Add(
                    LogEvents.Enrich(a =>
                    {
                        a(("regionId", regionId));
                        a(("applicationVersion", applicationVersion));
                        a(("websiteSiteName", websiteSiteName));
                        a(("id", options.Id));
                    }));
            }

            var shouldLogToFile = options.LogToFile;

#if DEBUG
            shouldLogToFile = true;
#endif 

            if (shouldLogToFile)
            {
                var log = new SerilogLoggerConfiguration()
                          .WriteTo
                          .RollingFileAlternate("./logs", outputTemplate: "{Message}{NewLine}")
                          .CreateLogger();

                var subscription = LogEvents.Subscribe(
                    e => log.Information(e.ToLogString()),
                    assembliesEmittingPocketLoggerLogs);

                disposables.Add(subscription);
                disposables.Add(log);
            }

            disposables.Add(
                LogEvents.Subscribe(e => Console.WriteLine(e.ToLogString()),
                                    assembliesEmittingPocketLoggerLogs));

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Warning($"{nameof(TaskScheduler.UnobservedTaskException)}", args.Exception);
                args.SetObserved();
            };

            if (options.ApplicationInsightsKey != null)
            {
                var telemetryClient = new TelemetryClient(new TelemetryConfiguration(options.ApplicationInsightsKey))
                {
                    InstrumentationKey = options.ApplicationInsightsKey
                };
                disposables.Add(telemetryClient.SubscribeToPocketLogger(assembliesEmittingPocketLoggerLogs));
            }

            Log.Event("AgentStarting");
        }

        public static IWebHost ConstructWebHost(StartupOptions options)
        {
            var disposables = new CompositeDisposable();
            StartLogging(disposables, options);

            if (options.Key is null)
            {
                Log.Trace("No Key Provided");
            }
            else
            {
                Log.Trace("Received Key: {key}", options.Key);
            }

            var webHost = new WebHostBuilder()
                          .UseKestrel()
                          .UseContentRoot(Directory.GetCurrentDirectory())
                          .ConfigureServices(c =>
                          {
                              if (!String.IsNullOrEmpty(options.ApplicationInsightsKey))
                              {
                                  c.AddApplicationInsightsTelemetry(options.ApplicationInsightsKey);
                              }

                              c.AddSingleton(options);
                          })
                          .UseEnvironment(options.Production
                                              ? EnvironmentName.Production
                                              : EnvironmentName.Development)
                          .UseStartup<Startup>()
                          .Build();

            return webHost;
        }
    }
}
