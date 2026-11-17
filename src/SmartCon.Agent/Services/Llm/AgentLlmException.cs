namespace SmartCon.Agent.Services.Llm;

public sealed class AgentLlmException : Exception
{
    public AgentLlmException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }
}