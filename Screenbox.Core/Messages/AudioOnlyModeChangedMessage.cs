using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Screenbox.Core.Messages;

public sealed class AudioOnlyModeChangedMessage : ValueChangedMessage<bool>
{
    public AudioOnlyModeChangedMessage(bool value) : base(value)
    {
    }
}
