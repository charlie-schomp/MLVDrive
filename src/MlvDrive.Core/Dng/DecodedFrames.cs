// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
namespace MlvDrive.Core.Dng;

/// <summary>
/// Decoded lossless frames, shared by every open clip: an editor reads a DNG in many small
/// pieces, often several frames at once, and each frame should be decoded only once.
/// Least recently used frames go first once the total passes <see cref="BudgetBytes"/>.
/// </summary>
public static class DecodedFrames
{
    public static long BudgetBytes = 512L << 20;

    sealed record Entry((object Owner, int Frame) Key, Lazy<ushort[]> Pixels, long Bytes);

    static readonly object Lock = new();
    static readonly LinkedList<Entry> Lru = new();
    static readonly Dictionary<(object, int), LinkedListNode<Entry>> Map = new();
    static long _bytes;

    /// <summary>The decoded frame, decoding it (once, however many readers ask) if it is not held.</summary>
    public static ushort[] Get(object owner, int frame, long bytes, Func<ushort[]> decode)
    {
        LinkedListNode<Entry>? node;
        lock (Lock)
        {
            if (Map.TryGetValue((owner, frame), out node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
            }
            else
            {
                node = Lru.AddFirst(new Entry((owner, frame), new Lazy<ushort[]>(decode), bytes));
                Map[(owner, frame)] = node;
                _bytes += bytes;
                while (_bytes > BudgetBytes && Lru.Count > 2) Drop(Lru.Last!);
            }
        }
        try
        {
            return node.Value.Pixels.Value;
        }
        catch
        {
            // a failed read (drive gone) must not stay cached
            lock (Lock)
            {
                if (Map.TryGetValue((owner, frame), out var current) && current == node) Drop(node);
            }
            throw;
        }
    }

    /// <summary>Forget every frame of one clip (it was closed or changed).</summary>
    public static void Forget(object owner)
    {
        lock (Lock)
        {
            for (var n = Lru.First; n is not null;)
            {
                var next = n.Next;
                if (ReferenceEquals(n.Value.Key.Owner, owner)) Drop(n);
                n = next;
            }
        }
    }

    static void Drop(LinkedListNode<Entry> node)
    {
        Lru.Remove(node);
        Map.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes;
    }
}
