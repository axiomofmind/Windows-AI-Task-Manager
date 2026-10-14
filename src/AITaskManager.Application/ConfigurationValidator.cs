namespace AITaskManager.Application;

public static class ConfigurationValidator
{
    public static IReadOnlyList<ConfigurationError> Validate(AITaskManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ConfigurationError>();

        if (options.SchemaVersion != AITaskManagerOptions.CurrentSchemaVersion)
        {
            errors.Add(new(
                "SchemaVersion",
                $"Expected schema version {AITaskManagerOptions.CurrentSchemaVersion}."));
        }

        ValidateServer(options.Server, errors);
        ValidateStorage(options.Storage, errors);
        ValidateCollection(options.Collection, errors);
        ValidateDiscovery(options.Discovery, errors);
        ValidateAlerts(options.Alerts, errors);
        ValidateActions(options.Actions, errors);

        return errors;
    }

    public static void ThrowIfInvalid(AITaskManagerOptions options)
    {
        var errors = Validate(options);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }
    }

    private static void ValidateServer(ServerOptions options, ICollection<ConfigurationError> errors)
    {
        if (!Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.Port is <= 0 or > 65535)
        {
            errors.Add(new("Server:ListenUrl", "Use an absolute HTTP or HTTPS URL with a valid port."));
            return;
        }

        if (!uri.IsLoopback)
        {
            errors.Add(new(
                "Server:ListenUrl",
                "Only loopback addresses are supported until remote authentication and TLS are implemented."));
        }

        for (var index = 0; index < options.AllowedOrigins.Count; index++)
        {
            if (!Uri.TryCreate(options.AllowedOrigins[index], UriKind.Absolute, out var origin) ||
                origin.Scheme is not ("http" or "https") ||
                !origin.IsLoopback)
            {
                errors.Add(new(
                    $"Server:AllowedOrigins:{index}",
                    "Use a loopback HTTP or HTTPS origin until remote authentication and TLS are implemented."));
            }
        }
    }

    private static void ValidateStorage(StorageOptions options, ICollection<ConfigurationError> errors)
    {
        if (string.IsNullOrWhiteSpace(options.DatabaseFileName) ||
            !string.Equals(Path.GetFileName(options.DatabaseFileName), options.DatabaseFileName, StringComparison.Ordinal) ||
            options.DatabaseFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errors.Add(new("Storage:DatabaseFileName", "Use a file name without directory components."));
        }

        if (options.MetricRetentionDays is < 1 or > 3650)
        {
            errors.Add(new("Storage:MetricRetentionDays", "Use a value from 1 through 3650 days."));
        }

        if (options.LogRetentionDays is < 1 or > 365)
        {
            errors.Add(new("Storage:LogRetentionDays", "Use a value from 1 through 365 days."));
        }
    }

    private static void ValidateCollection(CollectionOptions options, ICollection<ConfigurationError> errors)
    {
        ValidateSeconds(options.ActiveIntervalSeconds, "Collection:ActiveIntervalSeconds", 0.25, 60, errors);
        ValidateSeconds(options.IdleIntervalSeconds, "Collection:IdleIntervalSeconds", 1, 300, errors);
        ValidateSeconds(options.SourceTimeoutSeconds, "Collection:SourceTimeoutSeconds", 0.25, 60, errors);

        if (options.IdleIntervalSeconds < options.ActiveIntervalSeconds)
        {
            errors.Add(new(
                "Collection:IdleIntervalSeconds",
                "The idle interval cannot be shorter than the active interval."));
        }

        if (options.StaleAfterMultiplier is < 1.5 or > 20)
        {
            errors.Add(new("Collection:StaleAfterMultiplier", "Use a value from 1.5 through 20."));
        }
    }

    private static void ValidateDiscovery(DiscoveryOptions options, ICollection<ConfigurationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < options.RuntimeEndpoints.Count; index++)
        {
            var endpoint = options.RuntimeEndpoints[index];
            var prefix = $"Discovery:RuntimeEndpoints:{index}";

            if (string.IsNullOrWhiteSpace(endpoint.Name))
            {
                errors.Add(new($"{prefix}:Name", "A stable endpoint name is required."));
            }
            else if (!names.Add(endpoint.Name))
            {
                errors.Add(new($"{prefix}:Name", "Endpoint names must be unique."));
            }

            if (string.IsNullOrWhiteSpace(endpoint.Kind))
            {
                errors.Add(new($"{prefix}:Kind", "A runtime adapter kind is required."));
            }

            if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                errors.Add(new($"{prefix}:BaseUrl", "Use an absolute HTTP or HTTPS URL."));
            }
        }

        ValidateRoots(options.ModelRoots, "Discovery:ModelRoots", errors);
        ValidateRoots(options.TrainingRoots, "Discovery:TrainingRoots", errors);
        ValidateRoots(options.DatasetRoots, "Discovery:DatasetRoots", errors);
    }

    private static void ValidateRoots(
        IReadOnlyList<string> roots,
        string configurationPath,
        ICollection<ConfigurationError> errors)
    {
        for (var index = 0; index < roots.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(roots[index]))
            {
                errors.Add(new($"{configurationPath}:{index}", "A configured root cannot be empty."));
            }
        }
    }

    private static void ValidateAlerts(AlertOptions options, ICollection<ConfigurationError> errors)
    {
        if (options.GpuTemperatureWarningCelsius is < 40 or > 110)
        {
            errors.Add(new("Alerts:GpuTemperatureWarningCelsius", "Use a value from 40 through 110 Celsius."));
        }

        if (options.VramWarningPercent is < 1 or > 100)
        {
            errors.Add(new("Alerts:VramWarningPercent", "Use a value from 1 through 100 percent."));
        }
    }

    private static void ValidateActions(ActionOptions options, ICollection<ConfigurationError> errors)
    {
        if (options.PowerControl.ConfirmationSeconds is < 1 or > 300)
        {
            errors.Add(new("Actions:PowerControl:ConfirmationSeconds", "Use a value from 1 through 300 seconds."));
        }

        if (options.FanControl.WatchdogSeconds is < 1 or > 60)
        {
            errors.Add(new("Actions:FanControl:WatchdogSeconds", "Use a value from 1 through 60 seconds."));
        }

        if (options.FanControl.FailsafeTemperatureCelsius is < 50 or > 110)
        {
            errors.Add(new("Actions:FanControl:FailsafeTemperatureCelsius", "Use a value from 50 through 110 Celsius."));
        }

        if (!options.Enabled && options.PowerControlEnabled)
        {
            errors.Add(new("Actions:PowerControlEnabled", "Enable the action system before power control."));
        }

        if (!options.Enabled && options.FanControlEnabled)
        {
            errors.Add(new("Actions:FanControlEnabled", "Enable the action system before fan control."));
        }

    }

    private static void ValidateSeconds(
        double value,
        string path,
        double minimum,
        double maximum,
        ICollection<ConfigurationError> errors)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            errors.Add(new(path, $"Use a finite value from {minimum} through {maximum} seconds."));
        }
    }
}
