using System;
using System.IO;
using FusionComms;
using FusionComms.Configurations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Serilog;

public class Program
{
    public static void Main(string[] args)
    {
            // Load a local .env file (if present) so Environment variables are available.
            TryLoadDotEnv();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProcessId()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProcessName()
            .Enrich.WithProperty("ProjectName", "fusion-comms")
            .WriteTo.Console()
            .WriteTo.Seq("https://seq-log.reachcinema.io")
            .CreateLogger();
        try
        {
            CreateHostBuilder(args).Build().Run();
        }
        catch (Exception ex)
        {
            //NLog: catch setup errors
            if (ex.InnerException?.Message != null) Log.Error(ex, ex.InnerException?.Message);
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
            });

    private static void TryLoadDotEnv()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                var envPath = Path.Combine(dir, ".env");
                if (File.Exists(envPath))
                {
                    foreach (var raw in File.ReadAllLines(envPath))
                    {
                        var line = raw.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                            continue;
                        var idx = line.IndexOf('=');
                        if (idx <= 0) continue;
                        var key = line.Substring(0, idx).Trim();
                        var val = line.Substring(idx + 1).Trim();
                        if ((val.StartsWith("\"") && val.EndsWith("\"")) || (val.StartsWith("'") && val.EndsWith("'")))
                        {
                            val = val.Substring(1, val.Length - 2);
                        }
                        Environment.SetEnvironmentVariable(key, val);
                    }
                    break;
                }
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent)) break;
                dir = parent;
            }
        }
        catch
        {
            // ignore failures to load .env
        }
    }
}

//var builder = WebApplication.CreateBuilder(args);

//var conn = builder.Configuration.GetConnectionString("connectionString");
//// Add services to the container.

//builder.Services.AddAutoMapper(typeof(Program));
//builder.Services.ConfigureDbContext(conn);
//builder.Services.ConfigureAppSetting(builder.Configuration);
//builder.Services.ConfigureAuthentication(builder.Configuration);
//builder.Services.ConfigureServices();
//builder.Services.ConfigureAuthorization();
//builder.Services.ConfigureSwagger();
//builder.Services.ConfigureDefaultIdentity();
//builder.Services.AddControllers();
//// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
//builder.Services.AddEndpointsApiExplorer();
////builder.Services.AddSwaggerGen();

//var app = builder.Build();

//// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
//    app.UseSwagger();
//    app.UseSwaggerUI();
//}

//app.UseMiddleware<ExceptionMiddleware>();

//app.UseHttpsRedirection();

//app.UseAuthentication();

//app.UseAuthorization();

//app.UseCors(option => option.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

//app.MapControllers();

//app.Run();
