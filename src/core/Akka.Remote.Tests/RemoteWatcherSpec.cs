﻿using System;
using Akka.Actor;
using Akka.TestKit;
using Akka.Util.Internal;
using Xunit;

namespace Akka.Remote.Tests
{
    public class RemoteWatcherSpec : AkkaSpec
    {
        class TestActorProxy : UntypedActor
        {
            readonly ActorRef _testActor;

            public TestActorProxy(ActorRef TestActor)
            {
                _testActor = TestActor;
            }

            protected override void OnReceive(object message)
            {
                _testActor.Forward(message);    
            }
        }

        class MyActor : UntypedActor
        {
            protected override void OnReceive(object message)
            {
            }
        }

        public static TimeSpan TurnOff = TimeSpan.FromMinutes(5);

        private static IFailureDetectorRegistry<Address> CreateFailureDetectorRegistry()
        {
            return new DefaultFailureDetectorRegistry<Address>(() => new PhiAccrualFailureDetector(
                8,
                200,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(1)));
        }

        class TestRemoteWatcher : RemoteWatcher
        {
            public class AddressTerm
            {
                readonly Address _address;

                public AddressTerm(Address address)
                {
                    _address = address;
                }

                public Address Address
                {
                    get { return _address; }
                }

                public override bool Equals(object obj)
                {
                    var other = obj as AddressTerm;
                    if (other == null) return false;
                    return _address.Equals(other._address);
                }

                public override int GetHashCode()
                {
                    return _address.GetHashCode();
                }
            }

            public class Quarantined
            {
                readonly Address _address;
                readonly int? _uid;

                public Quarantined(Address address, int? uid)
                {
                    _address = address;
                    _uid = uid;
                }

                public Address Address
                {
                    get { return _address; }
                }

                public int? Uid
                {
                    get { return _uid; }
                }

                public override bool Equals(object obj)
                {
                    var other = obj as Quarantined;
                    if (other == null) return false;
                    return _address.Equals(other._address); //TODO: Ignoring uid? /&& _uid == other._uid;
                }

                public override int GetHashCode()
                {
                    unchecked
                    {
                        var hash = 17;
                        hash = hash*23 + _address.GetHashCode();
                        //TODO: Ignoring uid hash = hash*23 + _uid.GetHashCode();
                        return hash;
                    }
                }
            }

            public TestRemoteWatcher(TimeSpan heartbeatExpectedResponseAfter) 
                : base(
                    CreateFailureDetectorRegistry(), 
                    TurnOff, 
                    TurnOff,
                    heartbeatExpectedResponseAfter)
            {   
            }

            public TestRemoteWatcher() : this(TurnOff)
            {
            }

            protected override void PublishAddressTerminated(Address address)
            {
                // don't publish the real AddressTerminated, but a testable message,
                // that doesn't interfere with the real watch that is going on in the background
                Context.System.EventStream.Publish(new AddressTerm(address));
            }

            protected override void Quarantine(Address address, int? addressUid)
            {
                // don't quarantine in remoting, but publish a testable message
                Context.System.EventStream.Publish(new Quarantined(address, addressUid));
            }
        }

        public RemoteWatcherSpec()
            : base(@"
            akka {
                loglevel = INFO 
                log-dead-letters-during-shutdown = false
                actor.provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
                remote.helios.tcp = {
                    hostname = localhost
                    port = 0
                }
            }")
        {
            _remoteSystem = ActorSystem.Create("RemoteSystem", Sys.Settings.Config);
            _remoteAddress = _remoteSystem.AsInstanceOf<ExtendedActorSystem>().Provider.DefaultAddress;
            var remoteAddressUid = AddressUidExtension.Uid(_remoteSystem);

            //TODO: Mute dead letters?
            /*
            Seq(system, remoteSystem).foreach(muteDeadLetters(
                akka.remote.transport.AssociationHandle.Disassociated.getClass,
                akka.remote.transport.ActorTransportAdapter.DisassociateUnderlying.getClass)(_))
            */

            _heartbeatRspB = new RemoteWatcher.HeartbeatRsp(remoteAddressUid);
        }

        protected override void AfterAll()
        {
            Shutdown(_remoteSystem);
            base.AfterAll();
        }
        readonly ActorSystem _remoteSystem;
        readonly Address _remoteAddress;
        readonly RemoteWatcher.HeartbeatRsp _heartbeatRspB;

        private int RemoteAddressUid
        {
            get { return AddressUidExtension.Uid(_remoteSystem); }
        }

        private ActorRef CreateRemoteActor(Props props, string name)
        {
            _remoteSystem.ActorOf(props, name);
            Sys.ActorSelection(new RootActorPath(_remoteAddress) / "user" / name).Tell(new Identify(name), TestActor);
            return ExpectMsg<ActorIdentity>().Subject;
        }

        [Fact]
        public void ARemoteWatcherMustHaveCorrectInteractionWhenWatching()
        {
            var fd = CreateFailureDetectorRegistry();
            var monitorA = Sys.ActorOf(Props.Create<TestRemoteWatcher>(), "monitor1");
            //TODO: Better way to write this?
            var monitorB = CreateRemoteActor(new Props(new Deploy(), typeof(TestActorProxy), new[]{TestActor}), "monitor1");

            var a1 = Sys.ActorOf(Props.Create<MyActor>(), "a1");
            var a2 = Sys.ActorOf(Props.Create<MyActor>(), "a2");
            var b1 = CreateRemoteActor(Props.Create<MyActor>(), "b1");
            var b2 = CreateRemoteActor(Props.Create<MyActor>(), "b2");

            monitorA.Tell(new RemoteWatcher.WatchRemote(b1, a1));
            monitorA.Tell(new RemoteWatcher.WatchRemote(b2, a1));
            monitorA.Tell(new RemoteWatcher.WatchRemote(b2, a2));
            monitorA.Tell(RemoteWatcher.Stats.Empty, TestActor);
            // for each watchee the RemoteWatcher also adds its own watch: 5 = 3 + 2
            // (a1->b1), (a1->b2), (a2->b2)
            ExpectMsg(RemoteWatcher.Stats.Counts(5, 1));
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            monitorA.Tell(_heartbeatRspB, monitorB);
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));

            monitorA.Tell(new RemoteWatcher.UnwatchRemote(b1, a1));
            // still (a1->b2) and (a2->b2) left
            monitorA.Tell(RemoteWatcher.Stats.Empty, TestActor);
            ExpectMsg(RemoteWatcher.Stats.Counts(3, 1));
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));

            monitorA.Tell(new RemoteWatcher.UnwatchRemote(b2, a2));
            // still (a1->b2) left
            monitorA.Tell(RemoteWatcher.Stats.Empty, TestActor);
            ExpectMsg(RemoteWatcher.Stats.Counts(2, 1));
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));

            monitorA.Tell(new RemoteWatcher.UnwatchRemote(b2, a1));
            // all unwatched
            monitorA.Tell(RemoteWatcher.Stats.Empty, TestActor);
            ExpectMsg(RemoteWatcher.Stats.Empty);
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectNoMsg(TimeSpan.FromMilliseconds(100));

            // make sure nothing floods over to next test
            ExpectNoMsg(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void ARemoteWatcherMustGenerateAddressTerminatedWhenMissingHeartbeats()
        {
            var p = CreateTestProbe();
            var q = CreateTestProbe();
            Sys.EventStream.Subscribe(p.Ref, typeof (TestRemoteWatcher.AddressTerm));
            Sys.EventStream.Subscribe(q.Ref, typeof(TestRemoteWatcher.Quarantined));

            var monitorA = Sys.ActorOf(Props.Create<TestRemoteWatcher>(), "monitor4");
            var monitorB = CreateRemoteActor(new Props(new Deploy(), typeof(TestActorProxy), new[] { TestActor }), "monitor4");

            var a = Sys.ActorOf(Props.Create<MyActor>(), "a4");
            var b = CreateRemoteActor(Props.Create<MyActor>(), "b4");

            monitorA.Tell(new RemoteWatcher.WatchRemote(b, a));

            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(_heartbeatRspB, monitorB);
            ExpectNoMsg(TimeSpan.FromSeconds(1));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(_heartbeatRspB, monitorB);

            Within(TimeSpan.FromSeconds(10), () =>
            {
                AwaitAssert(() =>
                {
                    monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
                    ExpectMsg<RemoteWatcher.Heartbeat>();
                    //but no HeartbeatRsp
                    monitorA.Tell(RemoteWatcher.ReapUnreachableTick.Instance);
                    p.ExpectMsg(new TestRemoteWatcher.AddressTerm(b.Path.Address), TimeSpan.FromSeconds(1));
                    q.ExpectMsg(new TestRemoteWatcher.Quarantined(b.Path.Address, RemoteAddressUid), TimeSpan.FromSeconds(1));
                });
                return true;
            });

            ExpectNoMsg(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void ARemoteWatcherMustGenerateAddressTerminatedWhenMissingFirstHeartbeat()
        {
            var p = CreateTestProbe();
            var q = CreateTestProbe();
            Sys.EventStream.Subscribe(p.Ref, typeof (TestRemoteWatcher.AddressTerm));
            Sys.EventStream.Subscribe(q.Ref, typeof (TestRemoteWatcher.Quarantined));

            var fd = CreateFailureDetectorRegistry();
            var heartbeatExpectedResponseAfter = TimeSpan.FromSeconds(2);
            var monitorA = Sys.ActorOf(new Props(new Deploy(), typeof(TestRemoteWatcher), new object[] {heartbeatExpectedResponseAfter}), "monitor5");
            var monitorB = CreateRemoteActor(new Props(new Deploy(), typeof(TestActorProxy), new[] { TestActor }), "monitor5");

            var a = Sys.ActorOf(Props.Create<MyActor>(), "a5");
            var b = CreateRemoteActor(Props.Create<MyActor>(), "b5");

            monitorA.Tell(new RemoteWatcher.WatchRemote(b, a));

            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            // no HeartbeatRsp sent

            Within(TimeSpan.FromSeconds(20), () =>
            {
                AwaitAssert(() =>
                {
                    monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
                    ExpectMsg<RemoteWatcher.Heartbeat>();
                    //but no HeartbeatRsp
                    monitorA.Tell(RemoteWatcher.ReapUnreachableTick.Instance);
                    p.ExpectMsg(new TestRemoteWatcher.AddressTerm(b.Path.Address), TimeSpan.FromSeconds(1));
                    // no real quarantine when missing first heartbeat, uid unknown
                    q.ExpectMsg(new TestRemoteWatcher.Quarantined(b.Path.Address, null), TimeSpan.FromSeconds(1));
                });
                return true;
            });

            ExpectNoMsg(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void
            ARemoteWatcherMustGenerateAddressTerminatedForNewWatchAfterBrokenConnectionWasRestablishedAndBrokenAgain()
        {
            var p = CreateTestProbe();
            var q = CreateTestProbe();
            Sys.EventStream.Subscribe(p.Ref, typeof(TestRemoteWatcher.AddressTerm));
            Sys.EventStream.Subscribe(q.Ref, typeof(TestRemoteWatcher.Quarantined));

            var monitorA = Sys.ActorOf(Props.Create<TestRemoteWatcher>(), "monitor6");
            var monitorB = CreateRemoteActor(new Props(new Deploy(), typeof(TestActorProxy), new[] { TestActor }), "monitor6");

            var a = Sys.ActorOf(Props.Create<MyActor>(), "a6");
            var b = CreateRemoteActor(Props.Create<MyActor>(), "b6");

            monitorA.Tell(new RemoteWatcher.WatchRemote(b, a));

            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(_heartbeatRspB, monitorB);
            ExpectNoMsg(TimeSpan.FromSeconds(1));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(_heartbeatRspB, monitorB);

            Within(TimeSpan.FromSeconds(10), () =>
            {
                AwaitAssert(() =>
                {
                    monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
                    ExpectMsg<RemoteWatcher.Heartbeat>();
                    //but no HeartbeatRsp
                    monitorA.Tell(RemoteWatcher.ReapUnreachableTick.Instance);
                    p.ExpectMsg(new TestRemoteWatcher.AddressTerm(b.Path.Address), TimeSpan.FromSeconds(1));
                    q.ExpectMsg(new TestRemoteWatcher.Quarantined(b.Path.Address, RemoteAddressUid), TimeSpan.FromSeconds(1));
                });
                return true;
            });

            //real AddressTerminated would trigger Terminated for b6, simulate that here
            _remoteSystem.Stop(b);
            AwaitAssert(() =>
            {
                monitorA.Tell(RemoteWatcher.Stats.Empty, TestActor);
                ExpectMsg(RemoteWatcher.Stats.Empty);
            });
            ExpectNoMsg(TimeSpan.FromSeconds(2));

            //assume that connection comes up again, or remote system is restarted
            var c = CreateRemoteActor(Props.Create<MyActor>(), "c6");
            monitorA.Tell(new RemoteWatcher.WatchRemote(c,a));

            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(_heartbeatRspB, monitorB);
            ExpectNoMsg(TimeSpan.FromSeconds(1));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(_heartbeatRspB, monitorB);
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(RemoteWatcher.ReapUnreachableTick.Instance, TestActor);
            p.ExpectNoMsg(TimeSpan.FromSeconds(1));
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(_heartbeatRspB, monitorB);
            monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
            ExpectMsg<RemoteWatcher.Heartbeat>();
            monitorA.Tell(RemoteWatcher.ReapUnreachableTick.Instance, TestActor);
            p.ExpectNoMsg(TimeSpan.FromSeconds(1));
            q.ExpectNoMsg(TimeSpan.FromSeconds(1));

            //then stop heartbeating again; should generate a new AddressTerminated
            Within(TimeSpan.FromSeconds(10), () =>
            {
                AwaitAssert(() =>
                {
                    monitorA.Tell(RemoteWatcher.HeartbeatTick.Instance, TestActor);
                    ExpectMsg<RemoteWatcher.Heartbeat>();
                    //but no HeartbeatRsp
                    monitorA.Tell(RemoteWatcher.ReapUnreachableTick.Instance);
                    p.ExpectMsg(new TestRemoteWatcher.AddressTerm(b.Path.Address), TimeSpan.FromSeconds(1));
                    q.ExpectMsg(new TestRemoteWatcher.Quarantined(b.Path.Address, RemoteAddressUid), TimeSpan.FromSeconds(1));
                });
                return true;
            });

            //make sure nothing floods over to next test
            ExpectNoMsg(TimeSpan.FromSeconds(2));
        }

    }
}
