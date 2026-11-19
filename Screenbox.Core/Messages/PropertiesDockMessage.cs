using Screenbox.Core.Enums;
using Screenbox.Core.ViewModels;

namespace Screenbox.Core.Messages;

public sealed record PropertiesDockMessage(PropertiesDockMode Mode, MediaViewModel? Media, bool IsOpen = true);
