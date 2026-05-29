namespace Daxter.Core.Tests;

// Tests that mutate process environment variables share this collection so they
// never run concurrently with one another.
[CollectionDefinition("EnvSerial", DisableParallelization = true)]
public sealed class EnvSerialCollection;
