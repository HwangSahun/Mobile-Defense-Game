using System;
using UnityEngine;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityEditor.Experimental.U2D.Common
{
    public static class ImagePacker
    {
        const int k_MaxTextureSize = 8196;

        /// <summary>
        /// Given an array of rects, the method returns an array of rects arranged within outPackedWidth and outPackedHeight
        /// </summary>
        /// <param name="rects">Rects to pack</param>
        /// <param name="padding">Padding between each rect</param>
        /// <param name="outPackedRects">Rects arranged within outPackedWidth and outPackedHeight</param>
        /// <param name="outPackedWidth">Width of the packed rects</param>
        /// <param name="outPackedHeight">Height of the packed rects</param>
        public static void Pack(RectInt[] rects, int padding, out RectInt[] outPackedRects, out int outPackedWidth, out int outPackedHeight)
        {
            var packNode = InternalPack(rects, padding);
            outPackedWidth = packNode.rect.width;
            outPackedHeight = packNode.rect.height;
            var visitor = new CollectPackNodePositionVisitor();
            packNode.AcceptVisitor(visitor);
            outPackedRects = new RectInt[rects.Length];
            for (int i = 0; i < rects.Length; ++i)
                outPackedRects[i] = new RectInt(visitor.positions[i].x + padding, visitor.positions[i].y, rects[i].width, rects[i].height);
        }

        /// <summary>
        /// Packs image buffer into a single buffer. Image buffers are assumed to be 4 bytes per pixel in RGBA format
        /// </summary>
        /// <param name="buffers">Image buffers to pack</param>
        /// <param name="width">Image buffers width</param>
        /// <param name="height">Image buffers height</param>
        /// <param name="padding">Padding between each packed image</param>
        /// <param name="outPackedBuffer">Packed image buffer</param>
        /// <param name="outPackedBufferWidth">Packed image buffer's width</param>
        /// <param name="outPackedBufferHeight">Packed iamge buffer's height</param>
        /// <param name="outPackedRect">Location of each image buffers in the packed buffer</param>
        /// <param name="outUVTransform">Translation data from image original buffer to packed buffer</param>
        public static void Pack(NativeArray<Color32>[] buffers, int width, int height, int padding, out NativeArray<Color32> outPackedBuffer, out int outPackedBufferWidth, out int outPackedBufferHeight, out RectInt[] outPackedRect, out Vector2Int[] outUVTransform)
        {
            UnityEngine.Profiling.Profiler.BeginSample("Pack");
            // Determine the area that contains data in the buffer
            outPackedBuffer = default(NativeArray<Color32>);
            try
            {
                var tightRects = FindTightRectJob.Execute(buffers, width, height);
                Pack(tightRects, padding, out outPackedRect, out outPackedBufferWidth, out outPackedBufferHeight);
                outUVTransform = new Vector2Int[tightRects.Length];
                for (int i = 0; i < outUVTransform.Length; ++i)
                {
                    outUVTransform[i] = new Vector2Int(outPackedRect[i].x - tightRects[i].x, outPackedRect[i].y - tightRects[i].y);
                }
                outPackedBuffer = new NativeArray<Color32>(outPackedBufferWidth * outPackedBufferHeight, Allocator.Temp);
                Blit(outPackedBuffer, outPackedRect, outPackedBufferWidth, buffers, tightRects, width, padding);
            }
            catch (Exception ex)
            {
                if (outPackedBuffer.IsCreated)
                    outPackedBuffer.Dispose();
                throw ex;
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        static ImagePackNode InternalPack(RectInt[] rects, int padding)
        {
            var sortedRects = new ImagePackRect[rects.Length];
            for (int i = 0; i < rects.Length; ++i)
            {
                sortedRects[i] = new ImagePackRect();
                sortedRects[i].rect = rects[i];
                sortedRects[i].index = i;
            }
            Array.Sort<ImagePackRect>(sortedRects);
            var root = new ImagePackNode();
            root.rect = new RectInt(0, 0, 32, 32);

            for (int i = 0; i < rects.Length; ++i)
            {
                if (!root.Insert(sortedRects[i], padding)) // we can't fit
                {
                    int newWidth = 0, newHeight = 0;
                    if (root.rect.width < root.rect.height)
                        newWidth = (int)NextPowerOfTwo((ulong)root.rect.width + 1);
                    else
                        newHeight = (int)NextPowerOfTwo((ulong)root.rect.height + 1);
                    if (newWidth > k_MaxTextureSize || newHeight > k_MaxTextureSize)
                    {
                        Debug.LogAssertion("unable to pack, reached max texture size");
                        break;
                    }

                    root.AdjustSize(newWidth, newHeight);
                    --i;
                }
            }
            return root;
        }

        static unsafe void Blit(NativeArray<Color32> buffer, RectInt[] blitToArea, int bufferbytesPerRow, NativeArray<Color32>[] originalBuffer, RectInt[] blitFromArea, int bytesPerRow, int padding)
        {
            UnityEngine.Profiling.Profiler.BeginSample("Blit");
            var c = (Color32*)buffer.GetUnsafePtr();
            for (int bufferIndex = 0; bufferIndex < blitToArea.Length && bufferIndex < originalBuffer.Length && bufferIndex < blitFromArea.Length; ++bufferIndex)
            {
                var b = (Color32*)originalBuffer[bufferIndex].GetUnsafeReadOnlyPtr();
                var rectFrom = blitFromArea[bufferIndex];
                var rectTo = blitToArea[bufferIndex];
                for (int i = 0; i < rectFrom.height; ++i)
                {
                    for (int j = 0; j < rectFrom.width; ++j)
                    {
                        Color32 cc = b[(rectFrom.y + i) * bytesPerRow + rectFrom.x + j];
                        c[((rectTo.y + i) * bufferbytesPerRow) + rectTo.x + j] = cc;
                    }
                }
            }
            UnityEngine.Profiling.Profiler.EndSample();
        }

        internal static ulong NextPowerOfTwo(ulong v)
        {
            v -= 1;
            v |= v >> 16;
            v |= v >> 8;
            v |= v >> 4;
            v |= v >> 2;
            v |= v >> 1;
            return v + 1;
        }

        internal class ImagePackRect : IComparable<ImagePackRect>
        {
            public RectInt rect;
            public int index;

            public int CompareTo(ImagePackRect obj)
            {
                var lhsArea = rect.width * rect.height;
                var rhsArea = obj.rect.width * obj.rect.height;
                if (lhsArea > rhsArea)
                    return -1;
                if (lhsArea < rhsArea)
                    return 1;
                if (index < obj.index)
                    return -1;

                return 1;
            }
        }
    }
}
