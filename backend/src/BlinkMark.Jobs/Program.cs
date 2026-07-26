using BlinkMark.Infrastructure.Configuration;

// -----------------------------------------------------------------------------
// One image, two roles.
//
// `BlinkMark__Jobs__Role` selects which hosted service runs. The reconciliation role is a cron
// job that exits when it finishes; the notifications role is a long-running queue consumer that
// KEDA scales from zero. Building one image and selecting behaviour by configuration keeps their
// shared infrastructure genuinely shared rather than copied.
// -----------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables();
builder.Services.AddBlinkMarkInfrastructure(builder.Configuration);

var role = builder.Configuration["BlinkMark:Jobs:Role"] ?? "reconciliation";

switch (role.ToLowerInvariant())
{
    case "reconciliation":
        builder.Services.AddSingleton<BlinkMark.Jobs.Reconciliation.CommentTtlSync>();
        builder.Services.AddHostedService<BlinkMark.Jobs.Reconciliation.RetentionReconciler>();
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown job role '{role}'. Expected 'notifications' or 'reconciliation'.");
}

var host = builder.Build();
await host.RunAsync();
