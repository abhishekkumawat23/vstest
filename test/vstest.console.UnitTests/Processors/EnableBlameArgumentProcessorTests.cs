﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace vstest.console.UnitTests.Processors
{
    using System;
    using Microsoft.VisualStudio.TestPlatform.CommandLine.Processors;
    using Microsoft.VisualStudio.TestPlatform.Common;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using vstest.console.UnitTests.TestDoubles;
    using CommandLineResources = Microsoft.VisualStudio.TestPlatform.CommandLine.Resources.Resources;

    [TestClass]
    public class EnableBlameArgumentProcessorTests
    {
        private TestableRunSettingsProvider settingsProvider;
        private EnableBlameArgumentExecutor executor;
        private const string DefaultRunSettings = "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<RunSettings>\r\n  <DataCollectionRunSettings>\r\n    <DataCollectors ></DataCollectors>\r\n  </DataCollectionRunSettings>\r\n  <RunConfiguration><ResultsDirectory>C:\\dir\\TestResults</ResultsDirectory></RunConfiguration>\r\n  </RunSettings>";

        public EnableBlameArgumentProcessorTests()
        {
            this.settingsProvider = new TestableRunSettingsProvider();
            this.executor = new EnableBlameArgumentExecutor(this.settingsProvider);
            CollectArgumentExecutor.EnabledDataCollectors.Clear();
        }

        [TestMethod]
        public void GetMetadataShouldReturnEnableBlameArgumentProcessorCapabilities()
        {
            var processor = new EnableBlameArgumentProcessor();
            Assert.IsTrue(processor.Metadata.Value is EnableBlameArgumentProcessorCapabilities);
        }

        [TestMethod]
        public void GetExecuterShouldReturnEnableBlameArgumentProcessorCapabilities()
        {
            var processor = new EnableBlameArgumentProcessor();
            Assert.IsTrue(processor.Executor.Value is EnableBlameArgumentExecutor);
        }

        [TestMethod]
        public void CapabilitiesShouldReturnAppropriateProperties()
        {
            var capabilities = new EnableBlameArgumentProcessorCapabilities();

            Assert.AreEqual("/Blame", capabilities.CommandName);
            Assert.AreEqual(false, capabilities.IsAction);
            Assert.AreEqual(ArgumentProcessorPriority.Logging, capabilities.Priority);
            Assert.AreEqual(HelpContentPriority.EnableDiagArgumentProcessorHelpPriority, capabilities.HelpPriority);
            Assert.AreEqual(CommandLineResources.EnableBlameUsage, capabilities.HelpContentResourceName);

            Assert.AreEqual(false, capabilities.AllowMultiple);
            Assert.AreEqual(false, capabilities.AlwaysExecute);
            Assert.AreEqual(false, capabilities.IsSpecialCommand);
        }

        [TestMethod]
        public void InitializeShouldCreateEntryForBlameInRunSettingsIfNotAlreadyPresent()
        {
            var runsettingsString = string.Format(DefaultRunSettings, "");
            var runsettings = new RunSettings();
            runsettings.LoadSettingsXml(DefaultRunSettings);
            this.settingsProvider.SetActiveRunSettings(runsettings);

            this.executor.Initialize("");

            Assert.IsNotNull(this.settingsProvider.ActiveRunSettings);
            Assert.AreEqual("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<RunSettings>\r\n  <DataCollectionRunSettings>\r\n    <DataCollectors>\r\n      <DataCollector friendlyName=\"blame\" enabled=\"True\">\r\n        <Configuration>\r\n          <ResultsDirectory>C:\\dir\\TestResults</ResultsDirectory>\r\n        </Configuration>\r\n      </DataCollector>\r\n    </DataCollectors>\r\n  </DataCollectionRunSettings>\r\n  <RunConfiguration>\r\n    <ResultsDirectory>C:\\dir\\TestResults</ResultsDirectory>\r\n  </RunConfiguration>\r\n  <LoggerRunSettings>\r\n    <Loggers>\r\n      <Logger friendlyName=\"blame\" enabled=\"True\" />\r\n    </Loggers>\r\n  </LoggerRunSettings>\r\n</RunSettings>", this.settingsProvider.ActiveRunSettings.SettingsXml);
        }
    }
}
