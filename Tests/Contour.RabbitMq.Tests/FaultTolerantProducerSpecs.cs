﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Contour.Transport.RabbitMQ;
using Contour.Transport.RabbitMQ.Internal;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Contour.RabbitMq.Tests
{
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:ElementsMustBeDocumented", Justification = "Reviewed. Suppression is OK here."),
     SuppressMessage("StyleCop.CSharp.NamingRules", "SA1300:ElementMustBeginWithUpperCaseLetter", Justification = "Reviewed. Suppression is OK here."),
     Category("Unit")]
    public class FaultTolerantProducerSpecs
    {
        [Test]
        public void should_prohibit_operations_if_disposed()
        {
            var selector = new Mock<IProducerSelector>();
            var producer = new FaultTolerantProducer(selector.Object, 0, 0, 0);
            producer.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => producer.Send(new MessageExchange(new Message(MessageLabel.Empty, null))));
        }

        [Test]
        public void should_iterate_producers_on_send_failures()
        {
            const int Count = 5;

            var producers = Enumerable.Range(0, Count).Select(i =>
            {
                var mock = new Mock<IProducer>();
                mock
                    .Setup(p => p.Publish(It.IsAny<IMessage>()))
                    .Throws(new Exception("Publish error"));
                mock
                    .Setup(p => p.BrokerUrl).Returns(() => $"fake.url.{DateTime.Now.Ticks}");

                return mock.Object;
            });

            var selector = new RoundRobinSelector(new ConcurrentQueue<IProducer>(producers));
            var producer = new FaultTolerantProducer(selector, Count, 0, 0);

            var message = new Message<DummyRequest>(MessageLabel.Any, new DummyRequest(1));
            var exchange = new MessageExchange(message);

            try
            {
                producer.Send(exchange);
                Assert.Fail();
            }
            catch (FailoverException fex)
            {
                fex.Attempts.Should().Be(Count);
            }
        }

        [Test]
        public void should_aggregate_attempt_errors()
        {
            const int Count = 5;

            var producers = Enumerable.Range(0, Count).Select(i =>
            {
                var mock = new Mock<IProducer>();
                mock
                    .Setup(p => p.Publish(It.IsAny<IMessage>()))
                    .Throws(new Exception("Publish error"));
                mock
                    .Setup(p => p.BrokerUrl).Returns(() => $"fake.url.{DateTime.Now.Ticks}");

                return mock.Object;
            });

            var selector = new RoundRobinSelector(new ConcurrentQueue<IProducer>(producers));
            var producer = new FaultTolerantProducer(selector, Count, 0, 0);

            var message = new Message<DummyRequest>(MessageLabel.Any, new DummyRequest(1));
            var exchange = new MessageExchange(message);

            try
            {
                producer.Send(exchange);
                Assert.Fail();
            }
            catch (FailoverException fex)
            {
                fex.InnerException.Should().BeOfType<AggregateException>();
                var errors = (AggregateException)fex.InnerException;
                errors.InnerExceptions.Count.Should().Be(Count);
            }
        }

        [Test]
        public void should_increase_retry_delay_no_greater_then_max_delay_on_producer_failure()
        {
            const int RetryDelay = 5;
            const int Attempts = 3;
            const int ResetDelay = 10;

            var producerMock = new Mock<IProducer>();
            producerMock
                .Setup(p => p.Publish(It.IsAny<IMessage>()))
                .Throws(new Exception("Publish error"));

            var hash = producerMock.Object.GetHashCode();
            var selector = new Mock<IProducerSelector>();
            selector.Setup(s => s.Next()).Returns(() => producerMock.Object);
            
            var producer = new FaultTolerantProducer(selector.Object, Attempts, RetryDelay, ResetDelay);

            var message = new Message<DummyRequest>(MessageLabel.Any, new DummyRequest(1));
            var exchange = new MessageExchange(message);

            Assert.Throws<FailoverException>(() => producer.Send(exchange));
            var delays = producer.Delays.ToList();

            delays.Should().HaveCount(1);
            delays.Should().Contain(kv => kv.Key == hash);
            delays.First().Value.Should().BeGreaterThan(0).And.BeLessOrEqualTo(RetryDelay);
        }

        [Test]
        public void should_delay_sending_on_producer_failure()
        {
            const int RetryDelay = 5;
            const int Attempts = 3;
            const int ResetDelay = 0;

            var producerMock = new Mock<IProducer>();
            producerMock
                .Setup(p => p.Publish(It.IsAny<IMessage>()))
                .Throws(new Exception("Publish error"));

            var selector = new Mock<IProducerSelector>();
            selector.Setup(s => s.Next()).Returns(() => producerMock.Object);

            var producer = new FaultTolerantProducer(selector.Object, Attempts, RetryDelay, ResetDelay);

            var message = new Message<DummyRequest>(MessageLabel.Any, new DummyRequest(1));
            var exchange = new MessageExchange(message);

            var action = new Action(() =>
            {
                try
                {
                    producer.Send(exchange);
                }
                catch
                {
                    // ignored
                }
            });

            var overall = 0;
            Enumerable
                .Range(0, Attempts - 1)
                .Aggregate(
                    0,
                    (prev, cur) =>
                    {
                        var time = Math.Min(2 * (prev + 1), RetryDelay);
                        overall += time;
                        return time;
                    });

            action.ExecutionTimeOf(a => a()).ShouldNotExceed(TimeSpan.FromSeconds(overall + 1));
        }

        [Test]
        public void should_reset_retry_delay_on_producer_success()
        {
            const int RetryDelay = 5;
            const int Attempts = 3;
            const int ResetDelay = 0;

            var tcs = new TaskCompletionSource<bool>();
            tcs.SetResult(true);

            var producerMock = new Mock<IProducer>();
            producerMock
                .Setup(p => p.Publish(It.IsAny<IMessage>()))
                .Returns(tcs.Task);

            var hash = producerMock.Object.GetHashCode();
            var selector = new Mock<IProducerSelector>();
            selector.Setup(s => s.Next()).Returns(() => producerMock.Object);

            var producer = new FaultTolerantProducer(selector.Object, Attempts, RetryDelay, ResetDelay);

            var message = new Message<DummyRequest>(MessageLabel.Any, new DummyRequest(1));
            var exchange = new MessageExchange(message);

            Assert.DoesNotThrow(() => producer.Send(exchange));
            var delays = producer.Delays.ToList();

            delays.Should().HaveCount(1);
            delays.Should().Contain(kv => kv.Key == hash);
            delays.First().Value.Should().Be(0);
        }

        [Test]
        public void should_reset_retry_delay_after_inactivity_period()
        {
            const int RetryDelay = 1;
            const int Attempts = 1;
            const int InactivityResetDelay = 3;

            var tcs = new TaskCompletionSource<bool>();
            tcs.SetResult(true);

            var producerMock = new Mock<IProducer>();
            producerMock
                .Setup(p => p.Publish(It.IsAny<IMessage>()))
                .Returns(tcs.Task);

            var selector = new Mock<IProducerSelector>();
            selector.Setup(s => s.Next()).Returns(() => producerMock.Object);

            var producer = new FaultTolerantProducer(selector.Object, Attempts, RetryDelay, InactivityResetDelay);

            var message = new Message<DummyRequest>(MessageLabel.Any, new DummyRequest(1));
            var exchange = new MessageExchange(message);

            Assert.DoesNotThrow(() => producer.Send(exchange));
            Thread.Sleep(TimeSpan.FromSeconds(InactivityResetDelay + 1));

            var delays = producer.Delays;
            delays.Should().BeEmpty();
        }
    }
}
