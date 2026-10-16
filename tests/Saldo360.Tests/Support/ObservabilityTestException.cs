namespace Npgsql;

/// <summary>Tipo de teste para validar a classificação por dependência sem depender de mensagens.</summary>
public sealed class ObservabilityTestException(string message) : Exception(message);
