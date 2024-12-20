﻿using System;
using System.Collections.Generic;
using Wkx;

namespace B3dm.Tileset
{
    public class Tile
    {
        private string id;
        private BoundingBox3D bb;

        public Tile(string id, BoundingBox3D bb)
        {
            this.id = id;
            this.bb = bb;
        }

        public string Id {
            get { return id; }
        }

        public BoundingBox3D BoundingBox {
            get { return bb; }
            set { this.bb = value; }
        }


        public Boundingvolume Boundingvolume {get;set;}

        public int Lod { get; set; }

        public List<Tile> Children { get; set; }

        public double GeometricError { get; set; }
    }
}
