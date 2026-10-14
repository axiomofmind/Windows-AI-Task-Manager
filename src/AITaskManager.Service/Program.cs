using AITaskManager.Hosting;

var app = await ApplicationHost.BuildAsync(args);
await app.RunAsync();

public partial class Program;
