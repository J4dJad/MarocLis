using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;

namespace MarocLIS.Web.Components.Shared;

/// <summary>
/// Whether a fragment of the action bar draws anything: an element, a component other than an
/// <see cref="ActionButton"/> told to hide, or some text. The fragment is rendered into a builder of
/// its own and only its frames are read; nothing of it reaches the page.
/// </summary>
internal static class ActionBarContent
{
    public static bool Renders(RenderFragment? fragment)
    {
        if (fragment is null) return false;

#pragma warning disable BL0006 // The frames are only read, never kept nor changed.
        using var builder = new RenderTreeBuilder();
        fragment(builder);
        var frames = builder.GetFrames();

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    return true;

                case RenderTreeFrameType.Text when !string.IsNullOrWhiteSpace(frame.TextContent):
                case RenderTreeFrameType.Markup when !string.IsNullOrWhiteSpace(frame.MarkupContent):
                    return true;

                case RenderTreeFrameType.Component:
                    if (!IsHiddenButton(frames.Array, i, frame)) return true;
                    i += frame.ComponentSubtreeLength - 1;
                    break;
            }
        }

        return false;
#pragma warning restore BL0006
    }

#pragma warning disable BL0006
    private static bool IsHiddenButton(RenderTreeFrame[] frames, int index, RenderTreeFrame component)
    {
        if (component.ComponentType != typeof(ActionButton)) return false;

        for (var j = index + 1; j < index + component.ComponentSubtreeLength; j++)
        {
            var attribute = frames[j];
            if (attribute.FrameType != RenderTreeFrameType.Attribute) break;
            if (attribute.AttributeName == nameof(ActionButton.Hidden)) return attribute.AttributeValue is true;
        }

        return false;
    }
#pragma warning restore BL0006
}
