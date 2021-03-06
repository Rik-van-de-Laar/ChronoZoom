// --------------------------------------------------------------------------------------------------------------------
// <copyright company="Outercurve Foundation">
//   Copyright (c) 2013, The Outercurve Foundation
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Microsoft.Practices.TransientFaultHandling;
using Microsoft.Practices.EnterpriseLibrary.WindowsAzure.TransientFaultHandling;
using Microsoft.Practices.EnterpriseLibrary.WindowsAzure.TransientFaultHandling.AzureStorage;

namespace Chronozoom.Entities
{
    /// <summary>
    /// Thrown whenever a query detects that the storage is corrupted.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2237:MarkISerializableTypesWithSerializable")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    public class StorageCorruptedException : Exception
    {
    }

    /// <summary>
    /// Storage implementation for ChronoZoom based on Entity Framework.
    /// </summary>
    public partial class Storage : DbContext
    {
        // Retry strategy: retry 10 times, half a second apart.
        private static FixedInterval retryStrategy = new FixedInterval(10, TimeSpan.FromSeconds(0.5));

        // Retry policy using the retry strategy and the Windows Azure storage transient fault detection strategy.
        private static RetryPolicy retryPolicy = new RetryPolicy<StorageTransientErrorDetectionStrategy>(retryStrategy);


        private static readonly Lazy<int> StorageTimeout = new Lazy<int>(() =>
        {
            string storageTimeout = ConfigurationManager.AppSettings["StorageTimeout"];
            return string.IsNullOrEmpty(storageTimeout) ? 30 : int.Parse(storageTimeout, CultureInfo.InvariantCulture);
        });

        // Enables RI-Tree queries.
        private static readonly Lazy<bool> UseRiTreeQuery = new Lazy<bool>(() =>
        {
            string useRiTreeQuery = ConfigurationManager.AppSettings["UseRiTreeQuery"];

            return !string.IsNullOrEmpty(useRiTreeQuery) && bool.Parse(useRiTreeQuery);
        });

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline")]
        static Storage()
        {
            Trace = new TraceSource("Storage", SourceLevels.All);
        }

        public Storage()
        {
            Configuration.ProxyCreationEnabled = false;
            if (System.Configuration.ConfigurationManager.ConnectionStrings["Storage"].ProviderName.Equals("System.Data.​SqlClient"))
            {
                ((IObjectContextAdapter)this).ObjectContext.CommandTimeout = StorageTimeout.Value;
            }
        }

        public static TraceSource Trace { get; private set; }

        public DbSet<Bitmask> Bitmasks { get; set; }

        public DbSet<Timeline> Timelines { get; set; }

        public DbSet<Exhibit> Exhibits { get; set; }

        public DbSet<ContentItem> ContentItems { get; set; }

        public DbSet<Tour> Tours { get; set; }

        public DbSet<Bookmark> Bookmarks { get; set; }

        public DbSet<User> Users { get; set; }

        public DbSet<Member> Members { get; set; }

        public DbSet<Collection> Collections { get; set; }

        public DbSet<SuperCollection> SuperCollections { get; set; }

        public DbSet<Triple> Triples { get; set; }

        public DbSet<TripleObject> TripleObjects { get; set; }

        public DbSet<TriplePrefix> TriplePrefixes { get; set; }

        public Collection<Timeline> TimelinesQuery(Guid collectionId, decimal startTime, decimal endTime, decimal span, Guid? commonAncestor, int maxElements, int depth)
        {
            Dictionary<Guid, Timeline> timelinesMap = new Dictionary<Guid, Timeline>();

            List<Timeline> timelines;
            if (UseRiTreeQuery.Value)
            {
                Trace.TraceInformation("Using RI-Tree Query");
                timelines = FillTimelinesRiTreeQuery(collectionId, timelinesMap, startTime, endTime, span, commonAncestor, ref maxElements);
            }
            else
            {
                timelines = FillTimelinesRangeQuery(collectionId, timelinesMap, startTime, endTime, span, commonAncestor, depth, ref maxElements);
            }

            if (maxElements > 0)
            {
                FillTimelineRelations(timelinesMap, maxElements);
            }

            return new Collection<Timeline>(timelines);
        }

        /// <summary>
        /// This required for override default precision and scale of decimal/numeric type
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Timeline>().Property(x => x.FromYear).HasPrecision(18, 7);
            modelBuilder.Entity<Timeline>().Property(x => x.ToYear).HasPrecision(18, 7);
            modelBuilder.Entity<Exhibit>().Property(x => x.Year).HasPrecision(18, 7);
            modelBuilder.Entity<ContentItem>().Property(x => x.Year).HasPrecision(18, 7);
        }

        public IEnumerable<Timeline> RetrieveAllTimelines(Guid collectionId)
        {
            int maxAllElements = 0;
            IEnumerable<TimelineRaw> allTimelines = new TimelineRaw[0];
            Dictionary<Guid, Timeline> timelinesMap = new Dictionary<Guid, Timeline>();

            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      allTimelines = Database.SqlQuery<TimelineRaw>("SELECT * FROM Timelines WHERE Collection_ID = {0}", collectionId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            IEnumerable<Timeline> rootTimelines = FillTimelinesFromFlatList(allTimelines, timelinesMap, null, ref maxAllElements);
            FillTimelineRelations(timelinesMap, int.MaxValue);

            return rootTimelines;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        public IEnumerable<Timeline> TimelineSubtreeQuery(Guid collectionId, Guid? leastCommonAncestor, decimal startTime, decimal endTime, decimal minSpan, int maxElements)
        {
            int maxAllElements = 0;
            Dictionary<Guid, Timeline> timelinesMap = new Dictionary<Guid, Timeline>();
            IEnumerable<TimelineRaw> allTimelines = new TimelineRaw[0];

            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      if (System.Configuration.ConfigurationManager.ConnectionStrings["Storage"].ProviderName.Equals("System.Data.SqlClient"))
                      {
                          allTimelines = Database.SqlQuery<TimelineRaw>("EXEC TimelineSubtreeQuery {0}, {1}, {2}, {3}, {4}, {5}", collectionId, leastCommonAncestor, minSpan, startTime, endTime, maxElements);
                      }
                      else
                      {
                          allTimelines = Database.SqlQuery<TimelineRaw>("SELECT * FROM Timelines WHERE Collection_ID = {0}", collectionId);
                      }
                  });
            }
            catch (Exception e)
            {
                throw e;
            }


            IEnumerable<Timeline> rootTimelines = FillTimelinesFromFlatList(allTimelines, timelinesMap, null, ref maxAllElements);
            FillTimelineRelations(timelinesMap, int.MaxValue);

            return rootTimelines;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2233:OperationsShouldNotOverflow", MessageId = "FromYear+13700000001"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2233:OperationsShouldNotOverflow", MessageId = "ToYear+13700000001")]
        public static Int64 ForkNode(Int64 fromYear, Int64 toYear)
        {
            Int64 start = fromYear + 13700000001;  //note: this value must be a positive integer (sign bit must be 0)
            Int64 end = toYear + 13700000001;  //note: this value must be a positive integer (sign bit must be 0)
            Int64 node = ((start - 1) ^ end) >> 1;
            node = node | node >> 1;
            node = node | node >> 2;
            node = node | node >> 4;
            node = node | node >> 8;
            node = node | node >> 16;
            node = node | node >> 32;
            node = end & ~node;
            return node;
        }

        private void FillTimelineRelations(Dictionary<Guid, Timeline> timelinesMap, int maxElements)
        {
            if (!timelinesMap.Keys.Any())
                return;


            // Populate Exhibits
            string exhibitsQuery = string.Format(
                CultureInfo.InvariantCulture,
                "SELECT TOP({0}) *, Year as [Time] FROM Exhibits WHERE Timeline_Id IN ('{1}')",
                maxElements,
                string.Join("', '", timelinesMap.Keys.ToArray()));

            IEnumerable<ExhibitRaw> exhibitsRaw = new ExhibitRaw[0];

            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      exhibitsRaw = Database.SqlQuery<ExhibitRaw>(exhibitsQuery);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }


            Dictionary<Guid, Exhibit> exhibits = new Dictionary<Guid, Exhibit>();
            foreach (ExhibitRaw exhibitRaw in exhibitsRaw)
            {
                if (exhibitRaw.ContentItems == null)
                    exhibitRaw.ContentItems = new Collection<ContentItem>();

                if (timelinesMap.Keys.Contains(exhibitRaw.Timeline_ID))
                {
                    timelinesMap[exhibitRaw.Timeline_ID].Exhibits.Add(exhibitRaw);
                    exhibits[exhibitRaw.Id] = exhibitRaw;
                }
            }

            if (exhibits.Keys.Any())
            {
                // Populate Content Items
                string contentItemsQuery = string.Format(
                    CultureInfo.InvariantCulture,
                    @"
                        SELECT * 
                        FROM ContentItems 
                        WHERE Exhibit_Id IN ('{0}')
                        ORDER BY [Order] ASC
                    ",
                    string.Join("', '", exhibits.Keys.ToArray()));

                IEnumerable<ContentItemRaw> contentItemsRaw = new ContentItemRaw[0];
                try
                {
                    retryPolicy.ExecuteAction(
                      () =>
                      {
                          contentItemsRaw = Database.SqlQuery<ContentItemRaw>(contentItemsQuery);
                      });
                }
                catch (Exception e)
                {
                    throw e;
                }


                foreach (ContentItemRaw contentItemRaw in contentItemsRaw)
                {
                    if (exhibits.Keys.Contains(contentItemRaw.Exhibit_ID))
                    {
                        exhibits[contentItemRaw.Exhibit_ID].ContentItems.Add(contentItemRaw);
                    }
                }
            }
        }

        /// <summary>
        /// Keeping this commented until RI-Tree performance is validated.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        private List<Timeline> FillTimelinesRangeQuery(Guid collectionId, Dictionary<Guid, Timeline> timelinesMap, decimal startTime, decimal endTime, decimal span, Guid? commonAncestor, int depth, ref int maxElements)
        {
            string timelinesQuery = @"
                SELECT 
                    TOP({0}) *,
                    FromYear as [Start],
                    ToYear as [End]
                FROM Timelines,
                    (
                        SELECT TOP(1)
                            Id as AncestorId,
                            Depth as AncestorDepth
                        FROM Timelines 
                        WHERE 
                            (Timeline_Id Is NULL OR Id = {5})
                            AND Collection_Id = {4}
                        ORDER BY 
                            CASE WHEN Id = {5} THEN 1 ELSE 0 END DESC, 
                            CASE WHEN Id Is NULL THEN 1 ELSE 0 END DESC
                    ) as AncestorTimeline
                WHERE
                    Collection_Id = {4} AND
                    Depth < AncestorDepth + {6} AND
                    (
                        FromYear >= {1} AND 
                        ToYear <= {2} AND 
                        ToYear-FromYear >= {3} OR
                        Id = {5}
                    )
                 ORDER BY
                    CASE WHEN Timelines.Id = AncestorId THEN 1 ELSE 0 END DESC, 
                    CASE WHEN Timeline_Id = AncestorId THEN 1 ELSE 0 END DESC, 
                    ToYear-FromYear DESC";
            IEnumerable<TimelineRaw> timelinesRaw = new TimelineRaw[0];
            int maximum = maxElements;
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      timelinesRaw = Database.SqlQuery<TimelineRaw>(timelinesQuery, maximum, startTime, endTime, span, collectionId, commonAncestor, depth);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            return FillTimelinesFromFlatList(
                timelinesRaw,
                timelinesMap,
                commonAncestor,
                ref maxElements);
        }

        /// <summary>
        /// TODO (Yitao):
        ///     1) Investigate performance from FillTimelinesRiTreeQuery vs FillTimelinesRangeQuery.
        ///     2) Based on performance results, we will consider splitting queries into RI-Queries/Simple-Queries
        ///     3) Add references to understanding RI-Trees here.
        ///     4) Send code review and approve.
        /// </summary>
        private List<Timeline> FillTimelinesRiTreeQuery(Guid collectionId, Dictionary<Guid, Timeline> timelinesMap, decimal startTime, decimal endTime, decimal span, Guid? commonAncestor, ref int maxElements)
        {
            /* There are 4 cases of a given timeline intersecting the current canvas: [<]>, <[>], [<>], and <[]> (<> denotes the timeline, and [] denotes the canvas) */

            const string timelinesQuery = @"SELECT TOP({0}) * FROM (
                SELECT DISTINCT [Timelines].*, [Timelines].[FromYear] as [Start], [Timelines].[ToYear] as [End], [Timelines].[ToYear] - [Timelines].[FromYear] AS [TimeSpan] FROM [Timelines] JOIN
                (
                    SELECT ([b1] & CAST(({1} + 13700000001) AS BIGINT)) AS [node] FROM [Bitmasks] WHERE (CAST(({1} + 13700000001) AS BIGINT) & [b2]) <> 0
                ) AS [left_nodes] ON [Timelines].[ForkNode] = [left_nodes].[node] AND [Timelines].[ToYear] >= {1} AND [Timelines].[ToYear] - [Timelines].[FromYear] >= {3} AND [Timelines].[Collection_Id] = {4} OR [Timelines].[Id] = {5}
                UNION ALL
                SELECT DISTINCT [Timelines].*, [Timelines].[FromYear] as [Start], [Timelines].[ToYear] as [End], [Timelines].[ToYear] - [Timelines].[FromYear] AS [TimeSpan] FROM [Timelines] JOIN
                (
                    SELECT (([b1] & CAST(({2} + 13700000001) AS BIGINT)) | [b3]) AS [node] FROM [bitmasks] WHERE (CAST (({2} + 13700000001) AS BIGINT) & [b3]) = 0
                )
                AS [right_nodes] ON [Timelines].[ForkNode] = [right_nodes].[node] AND [Timelines].[FromYear] <= {2} AND [Timelines].[ToYear] - [Timelines].[FromYear] >= {3} AND [Timelines].[Collection_Id] = {4} OR [Timelines].[Id] = {5}
                UNION ALL
                SELECT DISTINCT [Timelines].*, [Timelines].[FromYear] as [Start], [Timelines].[ToYear] as [End], [Timelines].[ToYear] - [Timelines].[FromYear] AS [TimeSpan] FROM [Timelines] WHERE [Timelines].[ForkNode] BETWEEN ({1} + 13700000001) AND ({2} + 13700000001) AND [Timelines].[ToYear] - [Timelines].[FromYear] >= {3} AND [Timelines].[Collection_Id] = {4} OR [Timelines].[Id] = {5}
            )
            AS [CanvasTimelines] ORDER BY [CanvasTimelines].[TimeSpan] DESC 
            ";

            IEnumerable<TimelineRaw> timelinesRaw = new TimelineRaw[0];
            int maximum = maxElements;
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      timelinesRaw = Database.SqlQuery<TimelineRaw>(timelinesQuery, maximum, startTime, endTime, span, collectionId, commonAncestor);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            return FillTimelinesFromFlatList(
                timelinesRaw,
                timelinesMap,
                commonAncestor,
                ref maxElements);
        }

        private static List<Timeline> FillTimelinesFromFlatList(IEnumerable<TimelineRaw> timelinesRaw, Dictionary<Guid, Timeline> timelinesMap, Guid? commonAncestor, ref int maxElements)
        {
            List<Timeline> timelines = new List<Timeline>();
            Dictionary<Guid, Guid?> timelinesParents = new Dictionary<Guid, Guid?>();

            foreach (TimelineRaw timelineRaw in timelinesRaw)
            {
                if (timelineRaw.Exhibits == null)
                    timelineRaw.Exhibits = new Collection<Exhibit>();

                timelinesParents[timelineRaw.Id] = timelineRaw.Timeline_ID;
                timelinesMap[timelineRaw.Id] = timelineRaw;

                maxElements--;
            }

            // Build the timelines tree by assigning each timeline to its parent
            foreach (Timeline timeline in timelinesMap.Values)
            {
                Guid? parentId = timelinesParents[timeline.Id];

                // If the timeline should be a root timeline
                if (timeline.Id == commonAncestor || parentId == null || !timelinesMap.Keys.Contains((Guid)parentId))
                {
                    timelines.Add(timeline);
                }
                else
                {
                    Timeline parentTimeline = timelinesMap[(Guid)parentId];
                    if (parentTimeline.ChildTimelines == null)
                        parentTimeline.ChildTimelines = new Collection<Timeline>();

                    parentTimeline.ChildTimelines.Add(timeline);
                }
            }

            return timelines;
        }

        // Recursively deletes every child timeline and exhibit (with references and content items) form timeline with given guid. 
        public void DeleteTimeline(Guid id)
        {
            var timelineIDs = GetChildTimelinesIds(id); // list of ids of child timelines
            var exhibitIDs = GetChildExhibitsIds(id); // list of ids of exhibits

            // recursively delete timelines
            while (timelineIDs.Count != 0)
            {
                DeleteTimeline(timelineIDs.First());
                timelineIDs.RemoveAt(0);
            }

            // recursively delete exhibits
            while (exhibitIDs.Count != 0)
            {
                DeleteExhibit(exhibitIDs.First());
                exhibitIDs.RemoveAt(0);
            }

            Timeline removeTimeline = Timelines.Find(id);
            Timelines.Remove(removeTimeline);
        }

        // Deletes every content item and reference from exhibit with given guid.
        public void DeleteExhibit(Guid id)
        {
            var exhibitsIDs = GetChildContentItemsIds(id); // list of ids of content items

            // delete content items
            while (exhibitsIDs.Count != 0)
            {
                var e = ContentItems.Find(exhibitsIDs.First());
                ContentItems.Remove(e);
                exhibitsIDs.RemoveAt(0);
            }

            Exhibit deleteExhibit = Exhibits.Find(id);
            Exhibits.Remove(deleteExhibit);
        }

        /// <summary>
        /// Retrieves a path to an timeline, exhibit or content item.
        /// </summary>
        public string GetContentPath(Guid collectionId, Guid? contentId, string title)
        {
            string contentPath = string.Empty;

            ContentItemRaw contentItem = null;
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      contentItem = Database.SqlQuery<ContentItemRaw>("SELECT * FROM ContentItems WHERE Collection_Id = {0} AND (Id = {1} OR Title = {2})", collectionId, contentId, title).FirstOrDefault();
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            if (contentItem != null)
            {
                contentPath = "/" + contentItem.Id;
                contentId = contentItem.Exhibit_ID;
            }

            ExhibitRaw exhibit = null;
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      exhibit = Database.SqlQuery<ExhibitRaw>("SELECT * FROM Exhibits WHERE Collection_Id = {0} AND (Id = {1} OR Title = {2})", collectionId, contentId, title).FirstOrDefault();
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            if (exhibit != null)
            {
                contentPath = "/e" + exhibit.Id + contentPath;
                contentId = exhibit.Timeline_ID;
            }


            TimelineRaw timeline = null;
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      timeline = Database.SqlQuery<TimelineRaw>("SELECT * FROM Timelines WHERE Collection_Id = {0} AND (Id = {1} OR Title = {2})", collectionId, contentId, title).FirstOrDefault();
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            while (timeline != null)
            {
                contentPath = "/t" + timeline.Id + contentPath;
                try
                {
                    retryPolicy.ExecuteAction(
                      () =>
                      {
                          timeline = Database.SqlQuery<TimelineRaw>("SELECT * FROM Timelines WHERE Id = {0}", timeline.Timeline_ID).FirstOrDefault();
                      });
                }
                catch (Exception e)
                {
                    throw e;
                }
            }

            return contentPath.ToString(CultureInfo.InvariantCulture);
        }

        // Returns list of ids of chilt timelines of timeline with given id.
        private List<Guid> GetChildTimelinesIds(Guid id)
        {
            var timelines = new List<Guid>();

            string timelinesQuery = string.Format(
                CultureInfo.InvariantCulture,
                "SELECT * FROM Timelines WHERE Timeline_Id IN ('{0}')",
                string.Join("', '", id));


            IEnumerable<TimelineRaw> timelinesRaw = new TimelineRaw[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      timelinesRaw = Database.SqlQuery<TimelineRaw>(timelinesQuery);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            foreach (TimelineRaw timelineRaw in timelinesRaw)
                timelines.Add(timelineRaw.Id);

            return timelines;
        }

        // Returns list of ids of child exhibits of timeline with given id.
        private List<Guid> GetChildExhibitsIds(Guid id)
        {
            var exhibits = new List<Guid>();

            string exhibitsQuery = string.Format(
                CultureInfo.InvariantCulture,
                "SELECT *, Year as [Time] FROM Exhibits WHERE Timeline_Id IN ('{0}')",
                string.Join("', '", id));

            IEnumerable<ExhibitRaw> exhibitsRaw = new ExhibitRaw[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      exhibitsRaw = Database.SqlQuery<ExhibitRaw>(exhibitsQuery);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            foreach (ExhibitRaw exhibitRaw in exhibitsRaw)
                exhibits.Add(exhibitRaw.Id);

            return exhibits;
        }

        // Returns list of ids of child content items of exhibit with given id.
        private List<Guid> GetChildContentItemsIds(Guid id)
        {
            var contentItems = new List<Guid>();

            // Find child content items
            string contentItemsQuery = string.Format(
                    CultureInfo.InvariantCulture,
                    "SELECT * FROM ContentItems WHERE Exhibit_Id IN ('{0}')",
                    string.Join("', '", id));


            IEnumerable<ContentItemRaw> contentItemsRaw = new ContentItemRaw[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      contentItemsRaw = Database.SqlQuery<ContentItemRaw>(contentItemsQuery);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            foreach (ContentItemRaw contentItemRaw in contentItemsRaw)
                contentItems.Add(contentItemRaw.Id);
            return contentItems;
        }

        public TimelineRaw GetParentTimelineRaw(Guid timelineId)
        {
            IEnumerable<TimelineRaw> parentTimelinesRaw = new TimelineRaw[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      parentTimelinesRaw = Database.SqlQuery<TimelineRaw>("SELECT * FROM Timelines WHERE Id in (SELECT Timeline_Id FROM Timelines WHERE Id = {0})", timelineId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }
            return parentTimelinesRaw.FirstOrDefault();
        }

        public TimelineRaw GetExhibitParentTimeline(Guid exhibitId)
        {
            IEnumerable<TimelineRaw> parentTimelinesRaw = new TimelineRaw[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      parentTimelinesRaw = Database.SqlQuery<TimelineRaw>("SELECT * FROM Timelines WHERE Id in (SELECT Timeline_Id FROM Exhibits WHERE Id = {0})", exhibitId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }
            return parentTimelinesRaw.FirstOrDefault();
        }

        public ExhibitRaw GetContentItemParentExhibit(Guid contentItemId)
        {
            IEnumerable<ExhibitRaw> exhibitRaw = new ExhibitRaw[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      exhibitRaw = Database.SqlQuery<ExhibitRaw>("SELECT * FROM Exhibits WHERE Id in (SELECT Exhibit_Id FROM ContentItems WHERE Id = {0})", contentItemId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }
            return exhibitRaw.FirstOrDefault();
        }

        public Timeline GetRootTimelines(Guid collectionId)
        {
            IEnumerable<Timeline> rootCollectionTimeline = new Timeline[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      rootCollectionTimeline = Database.SqlQuery<Timeline>("SELECT * FROM Timelines WHERE Timeline_ID is NULL and Collection_ID = {0}", collectionId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            return rootCollectionTimeline.FirstOrDefault();
        }

        public Guid GetCollectionGuid(string title)
        {
            IEnumerable<Guid> collectionGuid = new Guid[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      collectionGuid = Database.SqlQuery<Guid>("SELECT Id FROM Collections WHERE Title = {0}", title);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            return collectionGuid.FirstOrDefault();
        }

        public Guid GetCollectionFromTimeline(Guid? timelineId)
        {
            if (timelineId == null)
            {
                return Guid.Empty;
            }

            IEnumerable<Guid> collectionGuid = new Guid[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      collectionGuid = Database.SqlQuery<Guid>("SELECT Collection_Id FROM Timelines WHERE Id = {0}", timelineId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            return collectionGuid.FirstOrDefault();
        }

        public Guid GetCollectionFromExhibitGuid(Guid exhibitId)
        {
            IEnumerable<Guid> collectionGuid = new Guid[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      collectionGuid = Database.SqlQuery<Guid>("SELECT Collection_Id FROM Exhibits WHERE Id = {0}", exhibitId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }

            return collectionGuid.FirstOrDefault();
        }

        public Guid GetCollectionFromContentItemGuid(Guid contentId)
        {
            IEnumerable<Guid> collectionGuid = new Guid[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      collectionGuid = Database.SqlQuery<Guid>("SELECT Collection_Id FROM ContentItems WHERE Id = {0}", contentId);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }
            return collectionGuid.FirstOrDefault();
        }

        // Returns the tour associated with a given bookmark id.
        public Tour GetBookmarkTour(Bookmark bookmark)
        {
            if (bookmark == null)
                return null;

            IEnumerable<Tour> bookmarkTour = new Tour[0];
            try
            {
                retryPolicy.ExecuteAction(
                  () =>
                  {
                      bookmarkTour = Database.SqlQuery<Tour>("SELECT * FROM Tours WHERE Id in (SELECT Tour_Id FROM Bookmarks WHERE Id = {0})", bookmark.Id);
                  });
            }
            catch (Exception e)
            {
                throw e;
            }
            return bookmarkTour.FirstOrDefault();
        }

        /// <summary>Get owner of the collection</summary>
        /// <param name="collection">Collection object. May be null.</param>
        /// <returns>String representation of owning user ID</returns>
        public string GetCollectionOwner(Collection collection)
        {
            if (collection == null)
                return null;
            Entry(collection).Reference(c => c.User).Load();
            return collection.User != null ? collection.User.Id.ToString() : null;
        }

        /// <summary>Gets owner of the triplet</summary>
        /// <param name="name">Name of the triplet. Triplets referring to timelines, exhibits, artifacts and bNodes are supported.
        /// In all other cases method returns null.</param>
        /// <param name="bNodes">List of previously examined bNodes to avoid endless recursion</param>
        /// <returns>String representation of owning user ID</returns>
        public string GetSubjectOwner(TripleName name, List<string> bNodes = null)
        {
            name = EnsurePrefix(name);
            switch (name.Prefix)
            {
                case TripleName.UserPrefix:
                    return name.Name;
                case TripleName.TimelinePrefix:
                    var timelineId = Guid.Parse(name.Name);
                    var timeline = Timelines.Where(t => t.Id == timelineId).FirstOrDefault();
                    if (timeline == null)
                        return null;
                    Entry(timeline).Reference(t => t.Collection).Load();
                    return GetCollectionOwner(timeline.Collection);
                case TripleName.ExhibitPrefix:
                    var exhibitId = Guid.Parse(name.Name);
                    var exhibit = Exhibits.Where(e => e.Id == exhibitId).FirstOrDefault();
                    if (exhibit == null)
                        return null;
                    Entry(exhibit).Reference(e => e.Collection).Load();
                    return GetCollectionOwner(exhibit.Collection);
                case TripleName.ArtifactPrefix:
                    var artifactId = Guid.Parse(name.Name);
                    var artifact = ContentItems.Where(c => c.Id == artifactId).FirstOrDefault();
                    if (artifact == null)
                        return null;
                    Entry(artifact).Reference(a => a.Collection).Load();
                    return GetCollectionOwner(artifact.Collection);
                case TripleName.TourPrefix:
                    var tourId = Guid.Parse(name.Name);
                    var tour = Tours.FirstOrDefault(t => t.Id == tourId);
                    if (tour == null)
                        return null;
                    Entry(tour).Reference(t => t.Collection).Load();
                    return GetCollectionOwner(tour.Collection);
                case "_":
                    var subject = name.ToString();
                    // Guard against infinite loop
                    if (bNodes != null && bNodes.Contains(subject))
                        return null;
                    // Find subject of any triple that uses passed subject as object
                    var linkedSubject = Triples.
                        Include(t => t.Objects).
                        Where(t => t.Objects.Any(o => o.Object == subject)).
                        Select(t => t.Subject).FirstOrDefault();
                    if (String.IsNullOrEmpty(linkedSubject))
                        return null;
                    // Get owner of linked subject
                    if (bNodes == null)
                        bNodes = new List<string>(new string[] { subject });
                    else
                        bNodes.Add(subject);
                    return GetSubjectOwner(TripleName.Parse(linkedSubject), bNodes);
                default:
                    return null;
            }
        }

    }
}