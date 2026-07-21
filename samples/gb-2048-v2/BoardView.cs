// Painting the board: each cell is a 2x2 block of its exponent's tile, spaced into a grid — the
// same layout the original sample proved on hardware. All writes go through Bg's deferred shadow,
// so this can run at any point in the frame; Video.EndFrame (inside Game.Run) flushes in vblank.
using Koh.GameBoy.Graphics;

namespace Koh.Samples.Gb2048V2;

static class BoardView
{
    public static void Draw(Board board)
    {
        for (byte row = 0; row < 4; row++)
        for (byte col = 0; col < 4; col++)
            Bg.Fill(ColOf(col), RowOf(row), 2, 2, board[col, row]);
    }

    private static byte ColOf(byte c) => (byte)(3 + c * 4);

    private static byte RowOf(byte r) => (byte)(3 + r * 3);
}
