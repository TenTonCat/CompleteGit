namespace CompleteGit.Git;

/// <summary>
/// Assigns commits to lanes for drawing a railway-style graph. Commits must be ordered so
/// every parent appears after its children (git log --date-order / --topo-order guarantee this).
/// Lanes are stable slots: a freed slot is reused by the next new lane, so columns never shift.
/// </summary>
public static class GraphBuilder
{
    public static int Build(IList<CommitInfo> commits)
    {
        var lanes = new List<string?>();
        var laneColors = new List<int>();
        int nextColor = 0;
        int maxLanes = 0;

        int Alloc(string hash)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is null)
                {
                    lanes[i] = hash;
                    laneColors[i] = nextColor++;
                    return i;
                }
            }
            lanes.Add(hash);
            laneColors.Add(nextColor++);
            return lanes.Count - 1;
        }

        foreach (var c in commits)
        {
            if (c.IsWorkingTree) { c.Graph = null; continue; }

            int lane = lanes.IndexOf(c.Hash);
            bool existed = lane >= 0;
            if (!existed) lane = Alloc(c.Hash);

            var row = new GraphRow { NodeLane = lane, NodeColor = laneColors[lane], IsMerge = c.Parents.Length > 1 };
            var freed = new List<int>();

            for (int j = 0; j < lanes.Count; j++)
            {
                if (lanes[j] is null) continue;
                if (j == lane)
                {
                    if (existed) row.Edges.Add(new GraphEdge(j, j, laneColors[j], EdgeKind.In));
                }
                else if (lanes[j] == c.Hash)
                {
                    row.Edges.Add(new GraphEdge(j, lane, laneColors[j], EdgeKind.In));
                    freed.Add(j);
                }
                else
                {
                    row.Edges.Add(new GraphEdge(j, j, laneColors[j], EdgeKind.Through));
                }
            }

            if (c.Parents.Length == 0)
            {
                lanes[lane] = null;
            }
            else
            {
                for (int k = 0; k < c.Parents.Length; k++)
                {
                    var p = c.Parents[k];
                    int existing = -1;
                    for (int j = 0; j < lanes.Count; j++)
                        if (j != lane && lanes[j] == p) { existing = j; break; }

                    if (k == 0)
                    {
                        if (existing >= 0)
                        {
                            row.Edges.Add(new GraphEdge(lane, existing, laneColors[lane], EdgeKind.Out));
                            lanes[lane] = null;
                        }
                        else
                        {
                            lanes[lane] = p;
                            row.Edges.Add(new GraphEdge(lane, lane, laneColors[lane], EdgeKind.Out));
                        }
                    }
                    else if (existing >= 0)
                    {
                        row.Edges.Add(new GraphEdge(lane, existing, laneColors[existing], EdgeKind.Out));
                    }
                    else
                    {
                        int slot = Alloc(p);
                        row.Edges.Add(new GraphEdge(lane, slot, laneColors[slot], EdgeKind.Out));
                    }
                }
            }

            foreach (var j in freed) lanes[j] = null;
            while (lanes.Count > 0 && lanes[^1] is null) { lanes.RemoveAt(lanes.Count - 1); laneColors.RemoveAt(laneColors.Count - 1); }

            int used = lanes.Count;
            foreach (var e in row.Edges) used = Math.Max(used, Math.Max(e.From, e.To) + 1);
            maxLanes = Math.Max(maxLanes, used);
            c.Graph = row;
        }
        return Math.Max(1, maxLanes);
    }
}
