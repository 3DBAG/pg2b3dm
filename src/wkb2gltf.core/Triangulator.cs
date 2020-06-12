﻿using System;
using System.Collections.Generic;
using Wkx;

namespace Wkb2Gltf
{
    public static class Triangulator
    {
        public static List<Triangle> GetTriangles(MultiPolygon polyhedralsurface, string[] hexColors, int batchId)
        {
            var degenerated_triangles = 0;
            var allTriangles = new List<Triangle>();
            for(var i=0;i<polyhedralsurface.Geometries.Count;i++) {
                var geometry = polyhedralsurface.Geometries[i];
                Triangle triangle;
                if (hexColors.Length > 0) {
                    if (hexColors.Length == 1) {
                        triangle = GetTriangle(geometry, batchId, hexColors[0]);
                    }
                    else {
                        if (hexColors.Length != polyhedralsurface.Geometries.Count) {
                            throw new ArgumentOutOfRangeException($"Expected number of colors: {polyhedralsurface.Geometries.Count}, actual: {hexColors.Length}");
                        }
                        triangle = GetTriangle(geometry, batchId, hexColors[i]);
                    }
                }
                else {
                    triangle = GetTriangle(geometry, batchId, String.Empty);
                }

                if (triangle != null) {
                    allTriangles.Add(triangle);
                }
                else {
                    degenerated_triangles++;
                }
            }

            return allTriangles;
        }


        public static Triangle GetTriangle(Polygon geometry, int batchId, string hexColor = "")
        {
            var pnts = geometry.ExteriorRing.Points;
            if (pnts.Count != 4) {
                throw new ArgumentOutOfRangeException($"Expected number of vertices in triangles: 4, actual: {pnts.Count}");
            }

            var triangle = new Triangle(pnts[0], pnts[1], pnts[2], batchId);
            if (hexColor != string.Empty) {
                triangle.Color = hexColor;
            }

            if (!triangle.IsDegenerated()) {
                return triangle;
            }
            return null;
        }
    }
}
