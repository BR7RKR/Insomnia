using Insomnia.Commands;
using Insomnia.DI;
using Insomnia.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var services = new ServiceCollection();
services.AddSingleton(TimeProvider.System);
services.AddSingleton<IShutdownTrackerService, ShutdownTrackerService>();
services.AddSingleton<ISleepTrackerService, SleepTrackerService>();


var registrar = new TypeRegistrar(services);

var app = new CommandApp(registrar);
app.Configure(config =>
    {
        config.AddCommand<KeepAliveCommand>("keep-alive");
    }
);

return app.Run(args);
