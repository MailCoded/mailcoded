namespace Mailcoded.Core.Tests.Support;

/// <summary>Unwraps a nullable in a test without leaving a nullable warning behind.</summary>
public static class Require
{
    public static T Value<T>(T? value, string what) where T : struct =>
        value ?? throw new InvalidOperationException($"Expected {what} to be present.");

    public static T Ref<T>(T? value, string what) where T : class =>
        value ?? throw new InvalidOperationException($"Expected {what} to be present.");
}
