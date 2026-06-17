namespace EldoradoApp.Models;

/// <summary>A volume discount tier: <paramref name="Percentage"/>% off at or above <paramref name="Quantity"/> units.</summary>
public sealed record VolumeDiscount(int Quantity, int Percentage);
