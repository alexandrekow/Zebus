﻿using System;
using System.Linq;
using Abc.Zebus.Directory;
using Abc.Zebus.Routing;
using Abc.Zebus.Testing.Extensions;
using Abc.Zebus.Testing.Measurements;
using Abc.Zebus.Tests.Messages;
using Abc.Zebus.Tests.Routing;
using NUnit.Framework;

namespace Abc.Zebus.Tests
{
    [TestFixture]
    public class SubscriptionTests
    {
        [SetUp]
        public void Setup()
        {
            _field = Environment.TickCount;
        }

        private int _field;

        [TestCase("whatever")]
        [TestCase("*")]
        [TestCase("#")]
        public void single_star_should_always_match(string routingKey)
        {
            var subscription = CreateSubscription("*");
            subscription.Matches(BindingKeyHelper.CreateFromString(routingKey, '.')).ShouldBeTrue();
        }

        [TestCase("whatever")]
        [TestCase("*")]
        [TestCase("#")]
        public void single_dash_should_always_match(string routingKey)
        {
            var subscription = CreateSubscription("#");
            subscription.Matches(BindingKeyHelper.CreateFromString(routingKey, '.')).ShouldBeTrue();
        }

        [TestCase("whatever")]
        [TestCase("*")]
        [TestCase("#")]
        public void empty_bindingKey_should_always_match(string routingKey)
        {
            var subscription = new Subscription(new MessageTypeId(typeof(FakeCommand)), BindingKey.Empty);
            subscription.Matches(BindingKeyHelper.CreateFromString(routingKey, '.')).ShouldBeTrue();
        }

        [TestCase("a.b.c")]
        [TestCase("b.c.d")]
        public void stars_should_always_match_if_same_number_of_parts(string routingKey)
        {
            var subscription = CreateSubscription("*.*.*");
            subscription.Matches(BindingKeyHelper.CreateFromString(routingKey, '.')).ShouldBeTrue();
        }

        [TestCase("a.b.*")]
        [TestCase("a.*.*")]
        [TestCase("a.*.c")]
        [TestCase("*.b.c")]
        public void binding_key_with_star_should_match_routing_key(string bindingKey)
        {
            var subscription = CreateSubscription(bindingKey);
            subscription.Matches(BindingKeyHelper.CreateFromString("a.b.c", '.')).ShouldBeTrue();
        }

        [TestCase("a.b.#")]
        [TestCase("a.#")]
        public void binding_key_with_dash_should_match_routing_key(string bindingKey)
        {
            var subscription = CreateSubscription(bindingKey);
            subscription.Matches(BindingKeyHelper.CreateFromString("a.b.c", '.')).ShouldBeTrue();
        }

        [TestCase("a.b", "a.b.c.d")]
        [TestCase("d.*", "a.b.c.d")]
        [TestCase("d.#", "a.b.c.d")]
        public void should_not_match_binding_key(string routingKey, string bindingKey)
        {
            var subscription = CreateSubscription(bindingKey);
            subscription.Matches(BindingKeyHelper.CreateFromString(routingKey, '.')).ShouldBeFalse();
        }

        [Test]
        public void exact_same_routing_key_should_match_binding_key()
        {
            var subscription = CreateSubscription("a.b.c");
            subscription.Matches(BindingKeyHelper.CreateFromString("a.b.c", '.')).ShouldBeTrue();
        }

        [Test]
        public void should_create_subscription_by_example()
        {
            var subscription = Subscription.ByExample(x => new FakeRoutableCommand(12, "name"));
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(BindingKeyHelper.CreateFromString("12.name.*", '.'));
        }

        [Test]
        public void should_create_subscription_by_example_with_field()
        {
            var subscription = Subscription.ByExample(x => new FakeRoutableCommand(_field, "name"));
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(new BindingKey(_field.ToString(), "name", "*"));
        }

        [Test]
        public void should_create_subscription_by_example_with_method()
        {
            var subscription = Subscription.ByExample(x => new FakeRoutableCommand(GetFieldValue(), "name"));
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(new BindingKey(_field.ToString(), "name", "*"));
        }

        [Test]
        public void should_create_subscription_by_example_with_placeholder()
        {
            var subscription = Subscription.ByExample(x => new FakeRoutableCommand(x.Any<decimal>(), "name"));
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(BindingKeyHelper.CreateFromString("*.name.*", '.'));
        }

        [Test]
        public void should_create_subscription_by_example_with_variable()
        {
            var value = Environment.TickCount;
            var subscription = Subscription.ByExample(x => new FakeRoutableCommand(value, "name"));
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(new BindingKey(value.ToString(), "name", "*"));
        }

        [Test]
        public void should_create_subscription_from_complex_predicate()
        {
            var otherId = Guid.NewGuid();

            var subscription = Subscription.Matching<FakeRoutableCommand>(x => x.Id == 12 && x.OtherId == otherId && x.Name == "name");
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(new BindingKey("12", "name", otherId.ToString()));
        }

        [Test]
        public void should_create_subscription_from_predicate_with_unary_expressions()
        {
            var subscription = Subscription.Matching<FakeRoutableCommandWithBoolean>(x => !x.IsAMatch);
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommandWithBoolean)));
            subscription.BindingKey.ShouldEqual(new BindingKey("False"));

            subscription = Subscription.Matching<FakeRoutableCommandWithBoolean>(x => x.IsAMatch);
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommandWithBoolean)));
            subscription.BindingKey.ShouldEqual(new BindingKey("True"));

            subscription = Subscription.Matching<FakeRoutableCommandWithBoolean>(x => !(!x.IsAMatch));
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommandWithBoolean)));
            subscription.BindingKey.ShouldEqual(new BindingKey("True"));
        }

        [Test]
        public void should_create_subscription_from_predicate_with_enum()
        {
            var subscription = Subscription.Matching<FakeRoutableCommandWithEnum>(x => x.Test1 == TestEnum1.Bar);
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommandWithEnum)));
            subscription.BindingKey.ShouldEqual(new BindingKey(TestEnum1.Bar.ToString(), "*"));
        }

        [Test]
        public void should_create_subscription_from_inverted_predicate_with_enum()
        {
            var subscription = Subscription.Matching<FakeRoutableCommandWithEnum>(x => TestEnum1.Bar == x.Test1);
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommandWithEnum)));
            subscription.BindingKey.ShouldEqual(new BindingKey(TestEnum1.Bar.ToString(), "*"));
        }

        [Test]
        public void should_create_subscription_from_complex_predicate_with_enum()
        {
            var subscription = Subscription.Matching<FakeRoutableCommandWithEnum>(x => x.Test1 == TestEnum1.Bar && x.Test2 == TestEnum2.Buz);
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommandWithEnum)));
            subscription.BindingKey.ShouldEqual(new BindingKey(TestEnum1.Bar.ToString(), TestEnum2.Buz.ToString()));
        }

        [Test]
        public void should_create_subscription_from_simple_predicate()
        {
            var subscription = Subscription.Matching<FakeRoutableCommand>(x => x.Id == GetFieldValue());
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(new BindingKey(GetFieldValue().ToString(), "*", "*"));
        }

        [Test]
        public void should_create_subscription_from_simple_predicate_in_generic_context()
        {
            var subscription = CreateSubscription();
            subscription.MessageTypeId.ShouldEqual(new MessageTypeId(typeof(FakeRoutableCommand)));
            subscription.BindingKey.ShouldEqual(new BindingKey(GetFieldValue().ToString(), "*", "*"));
        }

        [Test]
        public void should_be_equatable()
        {
            var otherId = Guid.NewGuid();

            var subscription1 = Subscription.Matching<FakeRoutableCommand>(x => x.Id == 12 && x.OtherId == otherId);
            var subscription2 = Subscription.Matching<FakeRoutableCommand>(x => x.Id == 12 && x.OtherId == otherId);

            subscription1.GetHashCode().ShouldEqual(subscription2.GetHashCode());
            subscription1.Equals(subscription2).ShouldBeTrue();
            subscription1.Equals((object)subscription2).ShouldBeTrue();
        }

        [Test]
        public void UpdatePeerSubscriptionsCommand_should_have_meaningful_to_string()
        {
            var id = Guid.NewGuid();
            var subscriptions = new[]
            {
                Subscription.Matching<FakeRoutableCommandWithEnum>(x => x.Test1 == TestEnum1.Bar && x.Test2 == TestEnum2.Buz),
                Subscription.Matching<FakeRoutableCommand>(x => x.Id == 12 && x.OtherId == id)
            };
            var peerId = new PeerId("Fake.Peer.Id");
            var command = new UpdatePeerSubscriptionsCommand(peerId, subscriptions, DateTime.Today);

            command.ToString()
                   .ShouldEqual(
                       $"PeerId: {peerId}, TimestampUtc: {DateTime.Today:yyyy-MM-dd HH:mm:ss.fff}, Subscriptions: [{string.Join(", ", subscriptions.AsEnumerable())}]");
        }

        [TestCase(nameof(FakeRoutableCommand.Id), "123")]
        [TestCase(nameof(FakeRoutableCommand.Name), "456")]
        [TestCase(nameof(FakeRoutableCommand.OtherId), "793e1561-26e4-4737-817f-996b986c1666")]
        public void should_get_part_for_member(string memberName, string expectedValue)
        {
            // Arrange
            var otherId = Guid.Parse("793e1561-26e4-4737-817f-996b986c1666");
            var subscription = Subscription.Matching<FakeRoutableCommand>(x => x.Id == 123 && x.Name == "456" && x.OtherId == otherId);

            // Act
            var part = subscription.GetBindingKeyPartForMember(memberName);

            // Assert
            part.ShouldEqual(BindingKeyPart.Parse(expectedValue));
        }

        [TestCase(nameof(FakeRoutableCommand.Id))]
        [TestCase(nameof(FakeRoutableCommand.Name))]
        [TestCase(nameof(FakeRoutableCommand.OtherId))]
        public void should_get_part_for_member_that_matches_all(string memberName)
        {
            // Arrange
            var subscription = Subscription.Any<FakeRoutableCommand>();

            // Act
            var part = subscription.GetBindingKeyPartForMember(memberName);

            // Assert
            part.MatchesAllValues.ShouldBeTrue();
        }

        [Test]
        public void should_not_get_part_for_unknown_member()
        {
            // Arrange
            var subscription = Subscription.Any<FakeRoutableCommand>();

            // Act, Assert
            Assert.Throws<InvalidOperationException>(() => subscription.GetBindingKeyPartForMember("something that will not match"));
        }

        [Test]
        [Explicit]
        [Category("ManualOnly")]
        [TestCase("a.b", "a.b.c.d")]
        [TestCase("d.*", "a.b.c.d")]
        [TestCase("d.#", "a.b.c.d")]
        public void MeasurePerformance(string routingKey, string bindingKey)
        {
            var subscription = CreateSubscription(bindingKey);
            var key = BindingKeyHelper.CreateFromString(routingKey, '.');

            Measure.Execution(x =>
            {
                x.Iteration = 1000000;
                x.WarmUpIteration = 1000;
                x.Action = _ => subscription.Matches(key);
            });
        }

        private Subscription CreateSubscription()
        {
            return Subscription.Matching<FakeRoutableCommand>(x => x.Id == GetFieldValue());
        }

        private Subscription CreateSubscription(string bindingKey)
        {
            return new Subscription(new MessageTypeId(typeof(FakeCommand)), BindingKeyHelper.CreateFromString(bindingKey, '.'));
        }

        private int GetFieldValue()
        {
            return _field;
        }
    }
}
