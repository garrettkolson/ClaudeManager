namespace ClaudeManager.Hub.Models;

public enum ToastKind { Info, Success, Warning, Error }

public record ToastMessage(Guid Id, string Title, string Body, ToastKind Kind)
{
    public static ToastMessage Create(string title, string body, ToastKind kind)
        => new(Guid.NewGuid(), title, body, kind);
}
