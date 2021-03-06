// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Clockwise;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Html;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.FSharp;
using Microsoft.DotNet.Interactive.Jupyter;
using Microsoft.DotNet.Interactive.Jupyter.Formatting;
using Microsoft.DotNet.Interactive.PowerShell;
using Microsoft.DotNet.Interactive.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Recipes;
using CommandHandler = System.CommandLine.Invocation.CommandHandler;

namespace Microsoft.DotNet.Interactive.App.CommandLine
{
    public static class CommandLineParser
    {
        public delegate void StartServer(
            StartupOptions options,
            InvocationContext context);

        public delegate Task<int> Jupyter(
            StartupOptions options,
            IConsole console,
            StartServer startServer = null,
            InvocationContext context = null);

        public delegate Task StartStdIO(
            StartupOptions options,
            KernelBase kernel,
            IConsole console);

        public static Parser Create(
            IServiceCollection services,
            StartServer startServer = null,
            Jupyter jupyter = null,
            StartStdIO startStdIO = null,
            ITelemetry telemetry = null,
            IFirstTimeUseNoticeSentinel firstTimeUseNoticeSentinel = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            startServer ??= (startupOptions, invocationContext) =>
                Program.ConstructWebHost(startupOptions).Run();

            jupyter ??= JupyterCommand.Do;

            startStdIO ??= StdIOCommand.Do;

            // Setup first time use notice sentinel.
            firstTimeUseNoticeSentinel ??= new FirstTimeUseNoticeSentinel(VersionSensor.Version().AssemblyInformationalVersion);

            // Setup telemetry.
            telemetry ??= new Telemetry.Telemetry(
                VersionSensor.Version().AssemblyInformationalVersion,
                firstTimeUseNoticeSentinel);
            var filter = new TelemetryFilter(Sha256Hasher.HashWithNormalizedCasing);

            var verboseOption = new Option<bool>(
                "--verbose",
                "Enable verbose logging to the console");

            var httpPortOption = new Option<HttpPort>(
                "--http-port",
                description: "Specifies the port on which to enable HTTP services",
                parseArgument: result =>
               {
                   var source = result.Tokens[0].Value;

                   if (source == "*")
                   {
                       return HttpPort.Auto;
                   }

                   if (!int.TryParse(source, out var portNumber))
                   {
                       result.ErrorMessage = "Must specify a port number or *.";
                       return null;
                   }

                   return new HttpPort(portNumber);
               });

            var httpPortRangeOption = new Option<PortRange>(
                "--http-port-range",
                parseArgument: result =>
                {
                    if (result.Parent.Parent.Children.FirstOrDefault(c => c.Symbol == httpPortOption) is OptionResult conflictingOption)
                    {
                        var parsed = result.Parent as OptionResult;
                        result.ErrorMessage = $"Cannot specify both {conflictingOption.Token.Value} and {parsed.Token.Value} together";
                        return null;
                    }

                    var source = result.Tokens[0].Value;

                    if (string.IsNullOrWhiteSpace(source))
                    {
                        result.ErrorMessage = "Must specify a port range";
                        return null;
                    }

                    var parts = source.Split(new[] { "-" }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length != 2)
                    {
                        result.ErrorMessage = "Must specify a port range";
                        return null;
                    }

                    if (!int.TryParse(parts[0], out var start) || !int.TryParse(parts[1], out var end))
                    {
                        result.ErrorMessage = "Must specify a port range as StartPort-EndPort";
                        return null;
                    }

                    if (start > end)
                    {
                        result.ErrorMessage = "Start port must be lower then end port";
                        return null;
                    }

                    var pr = new PortRange(start, end);
                    return pr;
                },
                description: "Specifies the range of port to use to enable HTTP services");

            var logPathOption = new Option<DirectoryInfo>(
                "--log-path",
                "Enable file logging to the specified directory");

            var pathOption = new Option<DirectoryInfo>(
                "--path",
                "Installs the kernelspecs to the specified directory")
                .ExistingOnly();

            var defaultKernelOption = new Option<string>(
                "--default-kernel",
                description: "The default language for the kernel",
                getDefaultValue: () => "csharp");

            var rootCommand = DotnetInteractive();

            rootCommand.AddCommand(Jupyter());
            rootCommand.AddCommand(StdIO());
            rootCommand.AddCommand(HttpServer());

            return new CommandLineBuilder(rootCommand)
                   .UseDefaults()
                   .UseMiddleware(async (context, next) =>
                   {
                       if (context.ParseResult.Errors.Count == 0)
                       {
                           telemetry.SendFiltered(filter, context.ParseResult);
                       }

                       // If sentinel does not exist, print the welcome message showing the telemetry notification.
                       if (!firstTimeUseNoticeSentinel.Exists() && !Telemetry.Telemetry.SkipFirstTimeExperience)
                       {
                           context.Console.Out.WriteLine();
                           context.Console.Out.WriteLine(Telemetry.Telemetry.WelcomeMessage);

                           firstTimeUseNoticeSentinel.CreateIfNotExists();
                       }

                       await next(context);
                   })
                   .Build();

            RootCommand DotnetInteractive()
            {
                var command = new RootCommand
                {
                    Name = "dotnet-interactive",
                    Description = "Interactive programming for .NET."
                };

                command.AddOption(logPathOption);
                command.AddOption(verboseOption);

                return command;
            }

            Command Jupyter()
            {
                var command = new Command("jupyter", "Starts dotnet-interactive as a Jupyter kernel")
                {
                    defaultKernelOption,
                    logPathOption,
                    verboseOption,
                    httpPortRangeOption,
                    new Argument<FileInfo>
                    {
                        Name = "connection-file"
                    }.ExistingOnly()
                };

                command.Handler = CommandHandler.Create<StartupOptions, JupyterOptions, IConsole, InvocationContext>(JupyterHandler);

                var installCommand = new Command("install", "Install the .NET kernel for Jupyter")
                {
                    logPathOption,
                    verboseOption,
                    httpPortRangeOption,
                    pathOption
                };

                installCommand.Handler = CommandHandler.Create<IConsole, InvocationContext, PortRange, DirectoryInfo>(InstallHandler);

                command.AddCommand(installCommand);

                return command;

                Task<int> JupyterHandler(StartupOptions startupOptions, JupyterOptions options, IConsole console, InvocationContext context)
                {
                    services = RegisterKernelInServiceCollection(services, startupOptions, options.DefaultKernel);

                    services.AddSingleton(c => ConnectionInformation.Load(options.ConnectionFile))
                        .AddSingleton<FrontendEnvironment>(c => c.GetService<BrowserFrontendEnvironment>())
                            .AddSingleton(c =>
                            {
                                return CommandScheduler.Create<JupyterRequestContext>(delivery => c.GetRequiredService<ICommandHandler<JupyterRequestContext>>()
                                                                                                   .Trace()
                                                                                                   .Handle(delivery));
                            })
                            .AddSingleton(c => new JupyterRequestContextHandler(
                                                  c.GetRequiredService<IKernel>())
                                              .Trace())
                            .AddSingleton<IHostedService, Shell>()
                            .AddSingleton<IHostedService, Heartbeat>();

                    return jupyter(startupOptions, console, startServer, context);
                }

                Task<int> InstallHandler(IConsole console, InvocationContext context, PortRange httpPortRange, DirectoryInfo location) =>
                    new JupyterInstallCommand(console, jupyterKernelSpecInstaller: new JupyterKernelSpecInstaller(console), httpPortRange, location).InvokeAsync();
            }

            Command HttpServer()
            {
                var command = new Command("http", "Starts dotnet-interactive with kernel functionality exposed over http")
                {
                    defaultKernelOption,
                    httpPortOption,
                    logPathOption,
                    httpPortRangeOption
                };

                command.Handler = CommandHandler.Create<StartupOptions, KernelHttpOptions, IConsole, InvocationContext>(
                    (startupOptions, options, console, context) =>
                    {
                        if (startupOptions.HttpPort == null && startupOptions.HttpPortRange == null)
                        {
                            startupOptions.HttpPort = HttpPort.Auto;
                        }

                        RegisterKernelInServiceCollection(services, startupOptions, options.DefaultKernel);

                        return jupyter(startupOptions, console, startServer, context);
                    });

                return command;
            }

            Command StdIO()
            {
                var command = new Command(
                    "stdio",
                    "Starts dotnet-interactive with kernel functionality exposed over standard I/O")
                {
                    defaultKernelOption,
                    logPathOption,
                    httpPortOption,
                    httpPortRangeOption
                };

                command.Handler = CommandHandler.Create<StartupOptions, StdIOOptions, IConsole, InvocationContext>(
                    (startupOptions, options, console, context) =>
                    {
                        if (startupOptions.EnableHttpApi)
                        {
                            RegisterKernelInServiceCollection(services, startupOptions, options.DefaultKernel,
                                kernel => StdIOCommand.CreateServer(kernel, console));
                            startServer?.Invoke(startupOptions, context);
                            return Task.FromResult(0);
                        }

                        return startStdIO(
                            startupOptions,
                            CreateKernel(options.DefaultKernel, new BrowserFrontendEnvironment(), startupOptions, null),
                            console);
                    });

                return command;
            }
        }

        private static IServiceCollection RegisterKernelInServiceCollection(IServiceCollection services, StartupOptions startupOptions, string defaultKernel, Action<KernelBase> afterKernelCreation = null)
        {
            services
                .AddSingleton(_ =>
                {
                    var frontendEnvironment = new BrowserFrontendEnvironment
                    {
                        ApiUri = new Uri($"http://localhost:{startupOptions.HttpPort.PortNumber}")
                    };
                    return frontendEnvironment;
                })
                .AddSingleton(c =>
                {
                    var frontendEnvironment = c.GetRequiredService<BrowserFrontendEnvironment>();
                    var kernel = CreateKernel(defaultKernel, frontendEnvironment, startupOptions,
                        c.GetRequiredService<HttpProbingSettings>());

                    afterKernelCreation?.Invoke(kernel);
                    return kernel;
                })
                .AddSingleton<KernelBase>(c => c.GetRequiredService<CompositeKernel>())
                .AddSingleton<IKernel>(c => c.GetRequiredService<KernelBase>());

            return services;
        }

        private static CompositeKernel CreateKernel(
            string defaultKernelName,
            FrontendEnvironment frontendEnvironment,
            StartupOptions startupOptions,
            HttpProbingSettings httpProbingSettings)
        {
            var compositeKernel = new CompositeKernel();
            compositeKernel.FrontendEnvironment = frontendEnvironment;

            compositeKernel.Add(
                new CSharpKernel()
                    .UseDefaultFormatting()
                    .UseNugetDirective()
                    .UseKernelHelpers()
                    .UseJupyterHelpers()
                    .UseWho()
                    .UseXplot()
                    .UseMathAndLaTeX(),
                new[] { "c#", "C#" });

            compositeKernel.Add(
                new FSharpKernel()
                    .UseDefaultFormatting()
                    .UseNugetDirective()
                    .UseKernelHelpers()
                    .UseWho()
                    .UseDefaultNamespaces()
                    .UseXplot()
                    .UseMathAndLaTeX(),
                new[] { "f#", "F#" });

            compositeKernel.Add(
                new PowerShellKernel()
                    .UseJupyterHelpers()
                    .UseXplot()
                    .UseProfiles(),
                new[] { "pwsh" });

            compositeKernel.Add(
                new JavaScriptKernel(),
                new[] { "js" });

            compositeKernel.Add(
                new HtmlKernel());

            var kernel = compositeKernel
                         .UseDefaultMagicCommands()
                         .UseLog()
                         .UseAbout();


            SetUpFormatters(frontendEnvironment, startupOptions);

            kernel.DefaultKernelName = defaultKernelName;

            if (startupOptions.EnableHttpApi)
            {
                kernel = kernel.UseHttpApi(startupOptions, httpProbingSettings);
                var enableHttp = new SubmitCode("#!enable-http", compositeKernel.Name);
                enableHttp.PublishInternalEvents();
                kernel.DeferCommand(enableHttp);
            }

            return kernel;
        }

        public static void SetUpFormatters(FrontendEnvironment frontendEnvironment, StartupOptions startupOptions)
        {
            switch (frontendEnvironment)
            {
                case AutomationEnvironment automationEnvironment:
                    break;

                case BrowserFrontendEnvironment browserFrontendEnvironment:
                    Formatter.DefaultMimeType = HtmlFormatter.MimeType;
                    Formatter.SetPreferredMimeTypeFor(typeof(LaTeXString), "text/latex");
                    Formatter.SetPreferredMimeTypeFor(typeof(MathString), "text/latex");
                    Formatter.SetPreferredMimeTypeFor(typeof(string), PlainTextFormatter.MimeType);
                    Formatter.SetPreferredMimeTypeFor(typeof(ScriptContent), HtmlFormatter.MimeType);

                    Formatter<LaTeXString>.Register((laTeX, writer) => writer.Write(laTeX.ToString()), "text/latex");
                    Formatter<MathString>.Register((math, writer) => writer.Write(math.ToString()), "text/latex");
                    if (startupOptions.EnableHttpApi)
                    {
                        Formatter<ScriptContent>.Register((script, writer) =>
                        {
                            var fullCode = $@"if (typeof window.createDotnetInteractiveClient === typeof Function) {{
createDotnetInteractiveClient('{browserFrontendEnvironment.ApiUri.AbsoluteUri}').then(function (interactive) {{
let notebookScope = getDotnetInteractiveScope('{browserFrontendEnvironment.ApiUri.AbsoluteUri}');
{script.ScriptValue}
}});
}}";
                            IHtmlContent content =
                                PocketViewTags.script[type: "text/javascript"](fullCode.ToHtmlContent());
                            content.WriteTo(writer, HtmlEncoder.Default);
                        }, HtmlFormatter.MimeType);
                    }
                    else
                    {
                        Formatter<ScriptContent>.Register((script, writer) =>
                        {
                            IHtmlContent content =
                                PocketViewTags.script[type: "text/javascript"](script.ScriptValue.ToHtmlContent());
                            content.WriteTo(writer, HtmlEncoder.Default);
                        }, HtmlFormatter.MimeType);
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(frontendEnvironment));
            }
        }
    }
}