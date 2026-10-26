namespace Uniform.Matrix.Hosting;

/// <summary>Marks an option that is read only at start, so changing it in the settings window takes effect after a restart.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RestartRequiredAttribute : Attribute;

/// <summary>Marks a secret option, such as a password, that the settings window neither shows nor saves.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;
