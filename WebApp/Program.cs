using PereneTts;
using PereneTts.Desktop;

// Finder cannot pass arguments, so the app bundle path also selects desktop mode. No await precedes this:
// the desktop window must run on the process main thread (AppKit).
if (DesktopLaunch.ShouldRun(args, AppContext.BaseDirectory)) return DesktopHost.Run();

var builder = WebApplication.CreateBuilder(args);
StudioApp.AddStudio(builder, new StudioHostOptions(new Uri(builder.Configuration["Tts:BaseUrl"] ?? "http://tts:5081/")));
var app = builder.Build();
StudioApp.MapStudio(app);
app.Run();
return 0;
