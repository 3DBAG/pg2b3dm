using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;

namespace Wkb2Gltf.Extensions
{
    public static class PrimitiveBuilderExtentionMethods
    {
        public static (int, int, int) AddTriangleWithBatchId(this PrimitiveBuilder<MaterialBuilder, VertexPosition, VertexWithBatchId, VertexEmpty> prim, (Vector3, Vector3, Vector3) triangle, int batchid)
        {
            var vertices = GetVertices(triangle, batchid);
            var res = prim.AddTriangle(vertices[0], vertices[1], vertices[2]);
            return res;
        }

        private static List<VertexBuilder<VertexPosition, VertexWithBatchId, VertexEmpty>> GetVertices((Vector3, Vector3, Vector3) triangle, int batchid)
        {
            var vb0 = GetVertexBuilder(triangle.Item1, batchid);
            var vb1 = GetVertexBuilder(triangle.Item2, batchid);
            var vb2 = GetVertexBuilder(triangle.Item3, batchid);
            return new List<VertexBuilder<VertexPosition, VertexWithBatchId, VertexEmpty>>() { vb0, vb1, vb2 };
        }

        private static VertexBuilder<VertexPosition, VertexWithBatchId, VertexEmpty> GetVertexBuilder(Vector3 position, int batchid)
        {
            var vp0 = new VertexPosition(position);
            var vb0 = new VertexBuilder<VertexPosition, VertexWithBatchId, VertexEmpty>(vp0, batchid);
            return vb0;
        }
    }
}
