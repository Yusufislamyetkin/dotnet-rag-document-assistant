namespace RagAssistant.Application.Abstractions;

public interface ICurrentUser
{
    Guid UserId { get; }
}
