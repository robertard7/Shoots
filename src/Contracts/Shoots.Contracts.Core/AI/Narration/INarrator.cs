namespace Shoots.Contracts.Core.AI.Narration;

public interface INarrator
{
    void Emit(NarrationEvent narrationEvent);
}
