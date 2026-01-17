using System;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullAiProvider : IAiProvider
{
    public AiResponse Invoke(AiRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        return new AiResponse("null");
    }
}
