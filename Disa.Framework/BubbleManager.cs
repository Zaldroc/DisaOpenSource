using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disa.Framework.Bubbles;

namespace Disa.Framework
{
    public static class BubbleManager
    {
        public static List<PresenceBubble> LastPresenceBubbles { get; private set; }

        static BubbleManager()
        {
            LastPresenceBubbles = new List<PresenceBubble>();
        }

        public static Task<bool> SendCompose(ComposeBubble composeBubble, ComposeBubbleGroup composeBubbleGroup)
        {
            return Send(composeBubble, composeBubbleGroup, false);
        }

        public static Task<bool> Send(Bubble b, bool resend = false)
        {
            return Send(b, null, resend);
        }

        private static Task<bool> Send(Bubble b, BubbleGroup group, bool resend)
        {
            return Task<bool>.Factory.StartNew(() =>
            {                   
                var vb = b as VisualBubble;
                if (vb != null)
                {
                    if (vb.Status == Bubble.BubbleStatus.Sent)
                    {
                        Utils.DebugPrint("Trying to send a bubble that is already sent! On " + vb.Service.Information.ServiceName);
                        return true;
                    }

                    Func<bool> restartServiceIfNeeded = () =>
                    {
                        if (!ServiceManager.IsRegistered(b.Service) ||
                            ServiceManager.IsRunning(b.Service) ||
                            ServiceManager.IsAborted(b.Service) )
                            return false;

                        Utils.DebugPrint(
                            "For fucks sakes. The scheduler isn't doing it's job properly, or " +
                            "you're sending a message to it at a weird time. Starting it up bra (" +
                            b.Service.Information.ServiceName + ").");
                        ServiceManager.AbortAndRestart(b.Service);
                        return true;
                    };

                    var visualBubbleServiceId = vb.Service as IVisualBubbleServiceId;
                    if (vb.IdService == null && vb.IdService2 == null && visualBubbleServiceId != null)
                    {
                        visualBubbleServiceId.AddVisualBubbleIdServices(vb);
                    }

                    try
                    {
                        @group = Group(vb, resend);
                    }
                    catch (Exception ex)
                    {
                        Utils.DebugPrint("Problem in Send:GroupBubble from service " + 
                                                 vb.Service.Information.ServiceName + ": " + ex.Message);
                        return false;
                    }

                    if (@group == null)
                    {
                        Utils.DebugPrint("Could not find a suitable group for bubble " + vb.ID 
                                                 + " on " + vb.Service.Information.ServiceName); 
                        return false;
                    }

                    var shouldQueue = vb.Service.QueuedBubblesParameters == null || 
                                      !vb.Service.QueuedBubblesParameters.BubblesNotToQueue.Contains(vb.GetType());

                    try
                    {
                        if (shouldQueue && !resend && 
                            BubbleQueueManager.HasQueuedBubbles(vb.Service.Information.ServiceName, 
                                true, false))
                        {
                            BubbleQueueManager.JustQueue(vb);
                            restartServiceIfNeeded();
                            return false;
                        }

                        if (shouldQueue)
                        {
                            Monitor.Enter(vb.Service.SendBubbleLock);
                        }

                        using (var queued = new BubbleQueueManager.InsertBubble(vb, shouldQueue))
                        {
                            Action checkForQueued = () =>
                            {
                                if (!resend 
                                    && BubbleQueueManager.HasQueuedBubbles(vb.Service.Information.ServiceName, true, true))
                                {
                                    BubbleQueueManager.Send(new [] { vb.Service.Information.ServiceName });
                                }
                            };

                            try
                            {
                                FailBubbleIfPathDoesntExist(vb);
                                SendBubbleInternal(b);
                            }
                            catch (ServiceQueueBubbleException ex)
                            {
                                Utils.DebugPrint("Queuing visual bubble on service " + 
                                                         vb.Service.Information.ServiceName + ": " + ex.Message);

                                UpdateStatus(vb, Bubble.BubbleStatus.Waiting, @group);

                                if (!restartServiceIfNeeded())
                                {
                                    checkForQueued();
                                }

                                return false;
                            }
                            catch (Exception ex)
                            {
                                queued.CancelQueueIfInsertable();

                                Utils.DebugPrint("Visual bubble on " + 
                                                         vb.Service.Information.ServiceName + " failed to be sent: " +
                                                         ex);

                                UpdateStatus(vb, Bubble.BubbleStatus.Failed, @group);
                                BubbleGroupEvents.RaiseBubbleFailed(vb, @group);

                                if (!restartServiceIfNeeded())
                                {
                                    checkForQueued();
                                }
                                    
                                //FIXME: if the bubble fails to send, allow the queue manager to continue.
                                if (resend)
                                    return true;

                                return false;
                            }
                                
                            queued.CancelQueueIfInsertable();

                            if (vb.Status == Bubble.BubbleStatus.Delivered)
                            {
                                Utils.DebugPrint(
                                    "************ Race condition. The server set the status to delivered/read before we could send it to sent. :')");
                                checkForQueued();
                                return true;
                            }

                            UpdateStatus(vb, Bubble.BubbleStatus.Sent, @group);

                            checkForQueued();
                            return true;
                        }
                    }
                    finally
                    {
                        if (shouldQueue)
                        {
                            Monitor.Exit(vb.Service.SendBubbleLock);
                        }
                    }
                }
                else
                {
                    var composeBubble = b as ComposeBubble;

                    if (composeBubble != null)
                    {
                        var bubbleToSend = composeBubble.BubbleToSend;
                        var visualBubbleServiceId = bubbleToSend.Service as IVisualBubbleServiceId;
                        if (bubbleToSend.IdService == null && bubbleToSend.IdService2 == null && 
                            visualBubbleServiceId != null)
                        {
                            visualBubbleServiceId.AddVisualBubbleIdServices(bubbleToSend);
                        }
                        @group.InsertByTime(bubbleToSend);
                        try
                        {
                            BubbleGroupEvents.RaiseBubbleInserted(bubbleToSend, @group);
                        }
                        catch
                        {
                            // do nothing
                        }
                    }

                    try
                    {
                        SendBubbleInternal(b);
                    }
                    catch (ServiceBubbleGroupAddressException ex)
                    {
                        if (!String.IsNullOrWhiteSpace(ex.Address))
                        {
                            if (composeBubble != null)
                            {
                                composeBubble.BubbleToSend.Address = ex.Address;
                                composeBubble.BubbleToSend.Status = Bubble.BubbleStatus.Sent;

                                var actualGroup = Group(composeBubble.BubbleToSend, resend);

                                ServiceEvents.RaiseComposeFinished(
                                    @group as ComposeBubbleGroup, actualGroup);

                                return true;
                            }
                        }

                        composeBubble.BubbleToSend.Status = Bubble.BubbleStatus.Failed;
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Utils.DebugPrint("Failed to send bubble on service " + b.Service.Information.ServiceName);

                        if (composeBubble != null)
                        {
                            composeBubble.BubbleToSend.Status = Bubble.BubbleStatus.Failed;
                        }

                        return false;
                    }

                    if (composeBubble != null)
                    {
                        composeBubble.BubbleToSend.Status = Bubble.BubbleStatus.Failed;
                        return false;
                    }

                    return true;
                }
            });
        }

        private static void SendBubbleInternal(Bubble b)
        {
            if (b.Service == null)
            {
                throw new ServiceBubbleSendFailedException("This function cannot be called with a null service.");
            }

            if (!b.Service.Information.DoesSupport(b.GetType()))
            {
                throw new ServiceBubbleSendFailedException("The service " + b.Service.Information.ServiceName +
                                                           " does not support " + b.GetType());
            }

            Utils.DebugPrint("Sending " + b.GetType().Name + " on service " + b.Service.Information.ServiceName);
            b.Service.SendBubble(b);
        }

        public static void SendSubscribe(Service service, bool subscribe)
        {
            if (!service.Information.DoesSupport(typeof(SubscribeBubble)))
                return;

            Utils.DebugPrint((subscribe
                ? "Subscribing"
                : "Unsubscribing") + " to " + service.Information.ServiceName
                                     + " solo bubble groups.");

            foreach (var bubbleGroup in BubbleGroupManager.FindAll(g => !g.IsParty && g.Service == service))
            {
                SendSubscribe(bubbleGroup, subscribe);
            }
        }

        public static void SendSubscribe(BubbleGroup bubbleGroup, bool subscribe)
        {
            if (!bubbleGroup.Service.Information.DoesSupport(typeof(SubscribeBubble)))
                return;

            var address = bubbleGroup.Address;

            var subcribeBubble = new SubscribeBubble(Time.GetNowUnixTimestamp(),
                Bubble.BubbleDirection.Outgoing, address,
                false, bubbleGroup.Service, subscribe);

            Send(subcribeBubble);
        }

        public static void SendPresence(Service service, bool available, bool justAdd = false)
        {
            if (!service.Information.DoesSupport(typeof(PresenceBubble)))
                return;

            var presenceBubble = new PresenceBubble(Time.GetNowUnixTimestamp(),
                Bubble.BubbleDirection.Outgoing, null,
                false, service, available);

            Utils.DebugPrint("Sending " + (presenceBubble.Available
                ? "available"
                : "unavailble") + " to " +
                                     presenceBubble.Service.Information.ServiceName);
            lock (LastPresenceBubbles)
            {
                LastPresenceBubbles.RemoveAll(pb => pb.Service == presenceBubble.Service);
                LastPresenceBubbles.Add(presenceBubble);
            }

            if (!justAdd && ServiceManager.IsRunning(service))
                Send(presenceBubble);

            if (available) return;
            foreach (var group in BubbleGroupManager.FindAll(service))
            {
                @group.PresenceType = PresenceBubble.PresenceType.Unavailable;
                //@group.Typing = false;
            }
        }

        public static void SendLastPresence(Service service)
        {
            if (!service.Information.DoesSupport(typeof(PresenceBubble)))
                return;

            lock (LastPresenceBubbles)
            {
                var presenceBubble = LastPresenceBubbles.FirstOrDefault(pb => pb.Service == service);
                if (presenceBubble == null)
                {
                    return;
                }
                LastPresenceBubbles.Remove(presenceBubble);

                Utils.DebugPrint("Sending last presence for service " + service.Information.ServiceName + ". " +
                                         (presenceBubble.Available ? "Available." : "Unavailable."));
                Send(presenceBubble);
            }
        }

        private static void FailBubbleIfPathDoesntExist(VisualBubble bubble)
        {
            var path = GetMediaFilePathIfPossible(bubble);
            if (String.IsNullOrWhiteSpace(path))
                return;
            if (!File.Exists(path))
            {
                throw new ServiceBubbleSendFailedException("Visual bubble media file path doesn't exist.");
            }
        }

        public static bool Update(BubbleGroup group, VisualBubble[] bubbles, int bubbleDepth = 100)
        {
            return BubbleGroupDatabase.UpdateBubble(@group, bubbles, bubbleDepth);
        }

        public static bool Update(BubbleGroup group, VisualBubble visualBubble, int bubbleDepth = 100)
        {
            if (visualBubble.BubbleGroupReference != null)
            {
                BubbleGroupDatabase.UpdateBubble(visualBubble.BubbleGroupReference, visualBubble, bubbleDepth);
                return true;
            }

            var unifiedGroup = @group as UnifiedBubbleGroup;
            if (unifiedGroup == null)
            {
                BubbleGroupDatabase.UpdateBubble(@group, visualBubble, bubbleDepth);
                return true;
            }

            foreach (var innerGroup in unifiedGroup.Groups)
            {
                foreach (var bubble in innerGroup)
                {
                    if (bubble.ID != visualBubble.ID) continue;

                    BubbleGroupDatabase.UpdateBubble(innerGroup, visualBubble, bubbleDepth);
                    return true;
                }
            }

            return false;
        }

        public static void UpdateStatus(VisualBubble b, Bubble.BubbleStatus status, BubbleGroup group, 
            bool updateBubbleGroupBubbles = true)
        {
            b.Status = status;
            BubbleGroupDatabase.UpdateBubble(@group, b);
            if (updateBubbleGroupBubbles)
                BubbleGroupEvents.RaiseBubblesUpdated(@group);
            BubbleGroupEvents.RaiseRefreshed(@group);
        }

        public static void SetNotQueuedToFailures(Service service)
        {
            if (service is UnifiedService)
                return;

            if (service.QueuedBubblesParameters == null 
                || service.QueuedBubblesParameters.SendingBubblesToFailOnServiceStart == null
                || !service.QueuedBubblesParameters.SendingBubblesToFailOnServiceStart.Any())
                return;

            foreach (var group in BubbleGroupManager.FindAll(service))
            {
                SetNotQueuedToFailures(@group);
            }
        }

        public static void SetNotQueuedToFailures(BubbleGroup group)
        {
            var groups = BubbleGroupManager.GetInner(@group).Where(x => !x.PartiallyLoaded && 
                (x.Service.QueuedBubblesParameters != null 
                && x.Service.QueuedBubblesParameters.SendingBubblesToFailOnServiceStart != null
                && x.Service.QueuedBubblesParameters.SendingBubblesToFailOnServiceStart.Any()));

            var failed = new List<Tuple<BubbleGroup, VisualBubble>>();

            foreach (var innerGroup in groups)
            {
                foreach (var bubble in innerGroup)
                {                        
                    if (bubble.Direction == Bubble.BubbleDirection.Outgoing && 
                        bubble.Status == Bubble.BubbleStatus.Waiting)
                    {
                        if (innerGroup
                            .Service.QueuedBubblesParameters.SendingBubblesToFailOnServiceStart
                            .FirstOrDefault(x => x == bubble.GetType()) != null)
                        {
                            failed.Add(new Tuple<BubbleGroup, VisualBubble>(innerGroup, bubble));
                        }
                    }
                }
            }

            if (!failed.Any())
                return;

            var somethingUpdated = false;

            var failuresGroupedByBubbleGroup = failed.GroupBy(x => x.Item1);
            foreach (var failureGroup in failuresGroupedByBubbleGroup)
            {
                var groupOfBubbles = failureGroup.First().Item1;
                var bubbles = failureGroup.Select(x => x.Item2).ToArray();

                foreach (var bubble in bubbles)
                {
                    bubble.Status = Bubble.BubbleStatus.Failed;
                }
                BubbleGroupDatabase.UpdateBubble(groupOfBubbles, bubbles, groupOfBubbles.Bubbles.Count + 100); // 100 is really a tolerance here (just in case)
                foreach (var bubble in bubbles)
                {
                    BubbleGroupEvents.RaiseBubbleFailed(bubble, groupOfBubbles);
                }
                somethingUpdated = true;
            }
                
            if (somethingUpdated)
            {
                BubbleGroupEvents.RaiseBubblesUpdated(@group);
                BubbleGroupEvents.RaiseRefreshed(@group);
            }
        }

        internal static void Replace(BubbleGroup group, IEnumerable<VisualBubble> bubbles)
        {
            var unifiedGroup = @group as UnifiedBubbleGroup;
            if (unifiedGroup != null)
            {
                foreach (var innerGroup in unifiedGroup.Groups)
                {
                    Replace(innerGroup, bubbles);
                }
            }

            for (int i = 0; i < @group.Bubbles.Count; i++)
            {
                var bubble = @group.Bubbles[i];
                var bubbleReplacement = bubbles.LastOrDefault(x => x.ID == bubble.ID);
                if (bubbleReplacement != null)
                {
                    @group.Bubbles.RemoveAt(i);
                    @group.Bubbles.Insert(i, bubbleReplacement);
                }
            }
        }

        public static void UpdateStatus(Service service, string bubbleId, Bubble.BubbleStatus status)
        {
            var serviceGroups = BubbleGroupManager.FindAll(service);
            foreach (var group in serviceGroups)
            {
                foreach (var bubble in @group)
                {
                    if (bubble.ID == bubbleId)
                    {
                        UpdateStatus(bubble, status, @group);
                        return;
                    }
                }
            }
        }

        private static bool IsBubbleDownloading(VisualBubble bubble)
        {
            var imageBubble = bubble as ImageBubble;
            if (imageBubble != null)
            {
                return imageBubble.Transfer != null;
            }
            var videoBubble = bubble as VideoBubble;
            if (videoBubble != null)
            {
                return videoBubble.Transfer != null;
            }
            var audioBubble = bubble as AudioBubble;
            if (audioBubble != null)
            {
                return audioBubble.Transfer != null;
            }
            var fileBubble = bubble as FileBubble;
            if (fileBubble != null)
            {
                return fileBubble.Transfer != null;
            }

            return false;
        }

        internal static IEnumerable<VisualBubble> FetchAllSendingAndDownloading(BubbleGroup group)
        {
            foreach (var bubble in @group)
            {
                if (bubble.Status == Bubble.BubbleStatus.Waiting 
                    && bubble.Direction == Bubble.BubbleDirection.Outgoing)
                {
                    yield return bubble;
                }
                else if (IsBubbleDownloading(bubble))
                {
                    yield return bubble;
                }
            }
        }

        public static string GetMediaFilePathIfPossible(VisualBubble bubble)
        {
            var imageBubble = bubble as ImageBubble;
            if (imageBubble != null)
            {
                return imageBubble.ImagePath;
            }

            var videoBubble = bubble as VideoBubble;
            if (videoBubble != null)
            {
                return videoBubble.VideoPath;
            }

            var audioBubble = bubble as AudioBubble;
            if (audioBubble != null)
            {
                return audioBubble.AudioPath;
            }

            var fileBubble = bubble as FileBubble;
            if (fileBubble != null)
            {
                return fileBubble.Path;
            }

            return null;
        }

        internal static BubbleGroup Group(VisualBubble vb, bool resend = false)
        {
            lock (BubbleGroupDatabase.OperationLock)
            {
                Utils.DebugPrint("Grouping an " + vb.Direction + " bubble on service " + vb.Service.Information.ServiceName);

                var theGroup =
                    BubbleGroupManager.FindWithAddress(vb.Service, vb.Address);

                BubbleGroupFactory.LoadFullyIfNeeded(theGroup);

                var duplicate = false;
                var newGroup = false;
                if (theGroup == null)
                {
                    Utils.DebugPrint(vb.Service.Information.ServiceName + " unable to find suitable group. Creating a new one.");

                    theGroup = new BubbleGroup(vb, null, false);

                    newGroup = true;

                    Utils.DebugPrint("GUID of new group: " + theGroup.ID);

                    vb.Service.NewBubbleGroupCreated(theGroup).ContinueWith(x =>
                    {
                        // force the UI to refetch the photo
                        theGroup.IsPhotoSetFromService = false;
                        SendSubscribe(theGroup, true);
                        BubbleGroupUpdater.Update(theGroup);
                    });

                    BubbleGroupManager.BubbleGroupsAdd(theGroup);
                }
                else
                {
                    if (resend)
                    {
                        if (vb.Status == Bubble.BubbleStatus.Failed)
                        {
                            UpdateStatus(vb, Bubble.BubbleStatus.Waiting, theGroup);
                        }
                        return theGroup;
                    }

                    var visualBubbleServiceId = vb.Service as IVisualBubbleServiceId;
                    if (visualBubbleServiceId != null && 
                        visualBubbleServiceId.DisctinctIncomingVisualBubbleIdServices())
                    {
                        if (vb.IdService != null)
                        {
                            duplicate = theGroup.FirstOrDefault(x => x.GetType() == vb.GetType() && x.IdService == vb.IdService) != null;
                        }
                        if (!duplicate && vb.IdService2 != null)
                        {
                            duplicate = theGroup.FirstOrDefault(x => x.GetType() == vb.GetType() && x.IdService2 == vb.IdService2) != null;
                        }
                    }

                    if (!duplicate)
                    {
                        Utils.DebugPrint(vb.Service.Information.ServiceName + " found a group. Adding.");

                        theGroup.InsertByTime(vb);
                    }
                    else
                    {
                        Utils.DebugPrint("Yuck. It's a duplicate bubble. No need to readd: " + vb.IdService + ", " + vb.IdService2);
                    }
                }

                if (!duplicate)
                {
                    Utils.DebugPrint("Inserting bubble into database group!");

                    try
                    {
                        if (newGroup)
                        {
                            BubbleGroupDatabase.AddBubble(theGroup, vb);
                        }
                        else
                        {
                            BubbleGroupDatabase.InsertBubbleByTime(theGroup, vb);
                        }
                    }
                    catch (Exception ex)
                    {
                        Utils.DebugPrint("Bubble failed to be inserting/added into the group " + theGroup.ID + ": " + ex);
                    }

                    try
                    {
                        BubbleGroupEvents.RaiseBubbleInserted(vb, theGroup);
                    }
                    catch (Exception ex)
                    {
                        Utils.DebugPrint(
                            "Error in notifying the interface that the bubble group has been updated (" +
                            vb.Service.Information.ServiceName + "): " + ex.Message);
                    }
                }

                return theGroup;
            }
        }

        public static List<VisualBubble> FindAll(Service service, string address)
        {
            var bubbleGroup = BubbleGroupManager.FindWithAddress(service, address);
            if (bubbleGroup == null)
            {
                return new List<VisualBubble>();
            }
            BubbleGroupFactory.LoadFullyIfNeeded(bubbleGroup);
            return bubbleGroup.Bubbles.ToList();
        }

        public static IEnumerable<VisualBubble> FindAllUnread(Service service, string address)
        {
            var bubbleGroup = BubbleGroupManager.FindWithAddress(service, address);
            if (bubbleGroup != null)
            {
                foreach (var bubble in bubbleGroup)
                {
                    if (bubble.Direction == Bubble.BubbleDirection.Incoming && 
                        bubble.Time >= BubbleGroupSettingsManager.GetLastUnreadSetTime(bubbleGroup))
                    {
                        yield return bubble;
                    }
                }
            }
        }
    }
}