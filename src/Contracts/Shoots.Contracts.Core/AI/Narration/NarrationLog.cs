using System.Collections.Generic;

namespace Shoots.Contracts.Core.AI.Narration;

public sealed record NarrationLog(IReadOnlyList<NarrationEvent> Events);
