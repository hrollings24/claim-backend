using Amazon.DynamoDBv2;
using Amazon.Lambda.AspNetCoreServer.Hosting;
using Amazon.Runtime;
using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Challenges;
using ClaimBackend.Api.Data;
using ClaimBackend.Api.Games;
using ClaimBackend.Api.Controllers;
using ClaimBackend.Api.Push;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

if (Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") is not null)
{
    builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
}

var cognitoOptions = builder.Configuration.GetSection(CognitoOptions.SectionName).Get<CognitoOptions>()
    ?? throw new InvalidOperationException($"Missing '{CognitoOptions.SectionName}' configuration section.");
builder.Services.Configure<CognitoOptions>(builder.Configuration.GetSection(CognitoOptions.SectionName));

// Enums travel as their names, not their numbers: the API already hands them out that way
// (Lobby, Claim, Inner), and without this they would only bind back from integers.
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Description = "Cognito access token. Enter as: Bearer {token}",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
        });
        return Task.CompletedTask;
    });
});

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.Authority = cognitoOptions.Authority;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = cognitoOptions.Authority,
            ValidateLifetime = true,
            // Cognito access tokens have no `aud` claim, so audience can't be validated here.
            // The app client is instead checked against `client_id` in OnTokenValidated below.
            ValidateAudience = false,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var clientId = context.Principal?.FindFirst("client_id")?.Value;
                if (clientId != cognitoOptions.AppClientId)
                {
                    context.Fail("Token was not issued for this app client.");
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddDbContext<ClaimBackendDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.Configure<GamesOptions>(builder.Configuration.GetSection(GamesOptions.SectionName));

builder.Services.AddSingleton<IAmazonDynamoDB>(serviceProvider =>
{
    var gamesOptions = serviceProvider.GetRequiredService<IOptions<GamesOptions>>().Value;

    if (string.IsNullOrWhiteSpace(gamesOptions.ServiceUrl))
    {
        // In AWS the region and credentials come from the Lambda execution environment.
        return new AmazonDynamoDBClient();
    }

    // DynamoDB Local accepts any credentials, but the SDK still insists on being given some.
    return new AmazonDynamoDBClient(
        new BasicAWSCredentials("local", "local"),
        new AmazonDynamoDBConfig { ServiceURL = gamesOptions.ServiceUrl });
});

builder.Services.AddSingleton<GameEngine>();
builder.Services.AddSingleton<GameStore>();

builder.Services.Configure<ChallengesOptions>(
    builder.Configuration.GetSection(ChallengesOptions.SectionName));

builder.Services.AddSingleton<ChallengeStore>();

builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.SectionName));

// A named client so push traffic gets its own connection pool and timeout, and a slow push
// service can never hold up the request that triggered it for long.
builder.Services.AddHttpClient(nameof(PushSender), client => client.Timeout = TimeSpan.FromSeconds(5));

builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<PushSender>();
builder.Services.AddSingleton<GameNotifier>();
builder.Services.AddSingleton<GameSweeper>();

builder.Services.Configure<MaintenanceOptions>(
    builder.Configuration.GetSection(MaintenanceOptions.SectionName));

// A comma-separated string rather than a JSON array so production can override it with a
// single Lambda environment variable (Cors__AllowedOrigins) instead of indexed array entries.
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"]
    ?? throw new InvalidOperationException("Missing 'Cors:AllowedOrigins' configuration value."))
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.AddPreferredSecuritySchemes("Bearer");
    });
}

app.UseHttpsRedirection();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
