namespace StoneshardTrials.Intro;

/// <summary>One picture of the intro and what is said over it.</summary>
/// <param name="File">The picture, relative to the intro's slides folder (16:9; scaled to cover the screen).</param>
/// <param name="Title">Shown in place of the picture when it is missing.</param>
/// <param name="Lines">Narration in the game's colour text, revealed one line after the other.</param>
/// <param name="Top">Fallback gradient colour at the top, 0xRRGGBB.</param>
/// <param name="Bottom">Fallback gradient colour at the bottom, 0xRRGGBB.</param>
/// <param name="PanX">Horizontal drift over the slide, -1 (to the left) to 1 (to the right).</param>
/// <param name="PanY">Vertical drift over the slide, -1 (up) to 1 (down).</param>
/// <param name="ZoomIn">Whether the camera pushes in (true) or pulls back (false).</param>
internal sealed record Slide(string File, string Title, string[] Lines, int Top, int Bottom, double PanX, double PanY, bool ZoomIn);

/// <summary>
/// The intro's story, in order. The pictures (intro_1.jpg ... intro_6.jpg) are
/// not in git: they are put in Intro\slides before a build, and a missing one
/// is drawn as a gradient with its title.
/// </summary>
internal static class Slides
{
    public static readonly Slide[] All =
    {
        new("intro_1.jpg", "The Road North",
            new[]
            {
                "Aldor is bleeding. Plague in the south, war in the north, and on every road a sellsword selling his blade.",
                "~gr~You were one of them, bound for the Skadian border with a debt behind you and nothing ahead.~/~",
            },
            0x222A3A, 0x0A0C12, PanX: 0.8, PanY: 0.1, ZoomIn: true),

        new("intro_2.jpg", "The Green Fire",
            new[]
            {
                "On the third night your campfire burned green, and the stars went out one by one.",
                "Then ~y~the old gods~/~ came for you: the ones who were here before the Church, before the first king.",
            },
            0x0C1A10, 0x030604, PanX: -0.4, PanY: -0.5, ZoomIn: true),

        new("intro_3.jpg", "The Old Gods",
            new[]
            {
                "Aldor has forgotten their names. They have not forgotten Aldor. They are hungry, and they are bored.",
                "~r~They want to watch a mortal fight for his life~/~, and they have chosen you.",
            },
            0x1A0E24, 0x06040A, PanX: 0, PanY: -0.7, ZoomIn: false),

        new("intro_4.jpg", "Osbrook Tavern",
            new[]
            {
                "You woke in Osbrook, in a tavern that stands between worlds. The fire is warm and the ale is real.",
                "~lg~Here you may rest, trade and mend your gear~/~, but you cannot leave the way you came.",
            },
            0x1E140E, 0x0A0604, PanX: 0.5, PanY: 0, ZoomIn: true),

        new("intro_5.jpg", "Every Door a Trial",
            new[]
            {
                "Every door out of the tavern leads to a trial: a crypt, a camp, a ruin no map remembers.",
                "~y~Win, and the gods reward you.~/~ ~r~Fall, and they find another.~/~",
            },
            0x12181C, 0x06080A, PanX: -0.7, PanY: 0.2, ZoomIn: false),

        new("intro_6.jpg", "Blood and Coin",
            new[]
            {
                "Their gifts are never free. Every boon is paid for in ~r~blood~/~ or in ~y~coin~/~.",
                "The gods are watching. ~w~The door awaits.~/~",
            },
            0x240808, 0x080202, PanX: 0, PanY: -0.3, ZoomIn: true),
    };
}
