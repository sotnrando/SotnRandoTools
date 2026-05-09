using System;
using NSubstitute;
using SotnApi.Interfaces;
using SotnRandoTools.Configuration.Interfaces;
using SotnRandoTools.Coop.Interfaces;
using SotnRandoTools.Services;
using Xunit;

namespace SotnRandoTools.Coop.Tests.CoopSenderTests
{
    public class ConstructorShould
    {
        [Fact]
        public void ThrowArgumentNullException_WhenToolConfigIsNull()
        {
            //Arrange
            var mockedToolConfig = Substitute.For<IToolConfig>();
            var mockedSotnApi = Substitute.For<ISotnApi>();
            var mockedCoopMessanger = Substitute.For<ICoopController>();
            var mockedNotificationService = Substitute.For<INotificationService>();
            //Act&Assert
            Assert.Throws<ArgumentNullException>(() => new CoopSender(null, mockedSotnApi, mockedNotificationService, mockedCoopMessanger));
        }

        [Fact]
        public void ThrowArgumentNullException_WhenSotnApiIsNull()
        {
            //Arrange
            var mockedToolConfig = Substitute.For<IToolConfig>();
            var mockedSotnApi = Substitute.For<ISotnApi>();
            var mockedCoopMessanger = Substitute.For<ICoopController>();
            var mockedNotificationService = Substitute.For<INotificationService>();
            //Act&Assert
            Assert.Throws<ArgumentNullException>(() => new CoopSender(mockedToolConfig, null, mockedNotificationService, mockedCoopMessanger));
        }

        [Fact]
        public void ThrowArgumentNullException_WhenCoopMessangerIsNull()
        {
            //Arrange
            var mockedToolConfig = Substitute.For<IToolConfig>();
            var mockedSotnApi = Substitute.For<ISotnApi>();
            var mockedCoopMessanger = Substitute.For<ICoopController>();
            var mockedNotificationService = Substitute.For<INotificationService>();
            //Act&Assert
            Assert.Throws<ArgumentNullException>(() => new CoopSender(mockedToolConfig, mockedSotnApi, mockedNotificationService, null));
        }

        [Fact]
        public void ReturnsAnInstance_WhenParametersAreNotNull()
        {
            //Arrange
            var mockedToolConfig = Substitute.For<IToolConfig>();
            var mockedSotnApi = Substitute.For<ISotnApi>();
            var mockedCoopMessanger = Substitute.For<ICoopController>();
            var mockedNotificationService = Substitute.For<INotificationService>();
            //Act
            CoopSender coopSender = new CoopSender(mockedToolConfig, mockedSotnApi, mockedNotificationService, mockedCoopMessanger);
            //Assert
            Assert.NotNull(coopSender);
        }
    }
}
