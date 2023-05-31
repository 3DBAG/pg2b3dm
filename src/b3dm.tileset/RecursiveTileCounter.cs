using System.Collections.Generic;

namespace B3dm.Tileset
{
    public static class RecursiveTileCounter
    {
        public static int CountTiles(List<Tile> tiles, int startValue)
        {
            foreach (var tile in tiles) {
                if ( !string.IsNullOrEmpty(tile.Id)) {
                    startValue++;
                }
                if (tile.Children != null) {
                    startValue = CountTiles(tile.Children, startValue);
                }
            }
            return startValue;
        }

    }
}
