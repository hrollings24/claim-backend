using Amazon.Lambda.AspNetCoreServer.Hosting;
using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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

builder.Services.AddControllers();

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
