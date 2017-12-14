// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Extensions.TrxLogger
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Xml;
    using Microsoft.TestPlatform.Extensions.TrxLogger.ObjectModel;
    using Microsoft.TestPlatform.Extensions.TrxLogger.Utility;
    using Microsoft.TestPlatform.Extensions.TrxLogger.XML;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.Utilities;
    using ObjectModel.Logging;
    using TrxLoggerConstants = Microsoft.TestPlatform.Extensions.TrxLogger.Utility.Constants;
    using TrxLoggerObjectModel = Microsoft.TestPlatform.Extensions.TrxLogger.ObjectModel;
    using TrxLoggerResources = Microsoft.VisualStudio.TestPlatform.Extensions.TrxLogger.Resources.TrxResource;

    /// <summary>
    /// Logger for Generating TRX
    /// </summary>
    [FriendlyName(TrxLoggerConstants.FriendlyName)]
    [ExtensionUri(TrxLoggerConstants.ExtensionUri)]
    internal class TrxLogger : ITestLoggerWithParameters
    {
        #region Fields

        /// <summary>
        /// Cache the TRX file path
        /// </summary>
        private string trxFilePath;

        private TrxLoggerObjectModel.TestRun testRun;
        private Dictionary<Guid, TrxLoggerObjectModel.ITestResult> results;
        private Dictionary<Guid, TrxLoggerObjectModel.ITestResult> innerResults;
        private Dictionary<Guid, TrxLoggerObjectModel.ITestElement> testElements;
        private Dictionary<Guid, TestEntry> entries;
        private Dictionary<Guid, TestEntry> innerTestEntries;

        /// <summary>
        /// Specifies the run level "out" messages
        /// </summary>
        private StringBuilder runLevelStdOut;

        // List of run level errors and warnings generated. These are logged in the Trx in the Results Summary.
        private List<TrxLoggerObjectModel.RunInfo> runLevelErrorsAndWarnings;

        private TrxLoggerObjectModel.TestOutcome testRunOutcome = TrxLoggerObjectModel.TestOutcome.Passed;

        private int totalTests, passTests, failTests;

        private DateTime testRunStartTime;

        /// <summary>
        /// Parameters dictionary for logger. Ex: {"LogFileName":"TestResults.trx"}.
        /// </summary>
        private Dictionary<string, string> parametersDictionary;

        /// <summary>
        /// Gets the directory under which default trx file and test results attachements should be saved.
        /// </summary>
        private string testResultsDirPath;

        #endregion

        #region ITestLogger

        /// <inheritdoc/>
        public void Initialize(TestLoggerEvents events, string testResultsDirPath)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (string.IsNullOrEmpty(testResultsDirPath))
            {
                throw new ArgumentNullException(nameof(testResultsDirPath));
            }

            // Register for the events.
            events.TestRunMessage += this.TestMessageHandler;
            events.TestResult += this.TestResultHandler;
            events.TestRunComplete += this.TestRunCompleteHandler;

            this.testResultsDirPath = testResultsDirPath;

            this.InitializeInternal();
        }

        /// <inheritdoc/>
        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (parameters.Count == 0)
            {
                throw new ArgumentException("No default parameters added", nameof(parameters));
            }

            this.parametersDictionary = parameters;
            this.Initialize(events, this.parametersDictionary[DefaultLoggerParameterNames.TestRunDirectory]);
        }
        #endregion

        #region ForTesting

        internal string GetRunLevelInformationalMessage()
        {
            return this.runLevelStdOut.ToString();
        }

        internal List<TrxLoggerObjectModel.RunInfo> GetRunLevelErrorsAndWarnings()
        {
            return this.runLevelErrorsAndWarnings;
        }

        internal DateTime TestRunStartTime
        {
            get { return this.testRunStartTime; }
        }

        internal TestRun LoggerTestRun
        {
            get { return this.testRun; }
        }

        internal int TotalTestCount
        {
            get { return totalTests; }
        }

        internal int PassedTestCount
        {
            get { return passTests; }
        }

        internal int FailedTestCount
        {
            get { return failTests; }
        }

        internal int TestResultCount
        {
            get { return this.results.Count; }
        }

        internal int UnitTestElementCount
        {
            get { return this.testElements.Count; }
        }

        internal int TestEntryCount
        {
            get { return this.entries.Count; }
        }

        internal TrxLoggerObjectModel.TestOutcome TestResultOutcome
        {
            get { return this.testRunOutcome; }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Called when a test message is received.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// Event args
        /// </param>
        public void TestMessageHandler(object sender, TestRunMessageEventArgs e)
        {
            System.Diagnostics.Debugger.Launch();
            ValidateArg.NotNull<object>(sender, "sender");
            ValidateArg.NotNull<TestRunMessageEventArgs>(e, "e");

            TrxLoggerObjectModel.RunInfo runMessage;
            switch (e.Level)
            {
                case TestMessageLevel.Informational:
                    this.AddRunLevelInformationalMessage(e.Message);
                    break;
                case TestMessageLevel.Warning:
                    runMessage = new TrxLoggerObjectModel.RunInfo(e.Message, null, Environment.MachineName, TrxLoggerObjectModel.TestOutcome.Warning);
                    this.runLevelErrorsAndWarnings.Add(runMessage);
                    break;
                case TestMessageLevel.Error:
                    this.testRunOutcome = TrxLoggerObjectModel.TestOutcome.Failed;
                    runMessage = new TrxLoggerObjectModel.RunInfo(e.Message, null, Environment.MachineName, TrxLoggerObjectModel.TestOutcome.Error);
                    this.runLevelErrorsAndWarnings.Add(runMessage);
                    break;
                default:
                    Debug.Fail("TrxLogger.TestMessageHandler: The test message level is unrecognized: {0}", e.Level.ToString());
                    break;
            }
        }

        /// <summary>
        /// Called when a test result is received.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// The eventArgs.
        /// </param>
        public void TestResultHandler(object sender, ObjectModel.Logging.TestResultEventArgs e)
        {
            // Create test run
            if (this.testRun == null)
                CreateTestRun();

            // Convert skipped test to a log entry as that is the behaviour of mstest.
            if (e.Result.Outcome == ObjectModel.TestOutcome.Skipped)
                this.HandleSkippedTest(e.Result);

            var testType = Converter.GetTestType(e.Result);
            var executionId = Converter.GetExecutionId(e.Result);

            // Setting parent properties like parent result, parent test element, parent execution id.
            var parentExecutionId = Converter.GetParentExecutionId(e.Result);
            var parentTestResult = GetTestResult(parentExecutionId);
            var parentTestElement = (parentTestResult != null) ? GetTestElement(parentTestResult.Id.TestId) : null;
            if (parentTestResult == null || parentTestElement == null || parentExecutionId == Guid.Empty)
            {
                parentTestResult = null;
                parentTestElement = null;
                parentExecutionId = Guid.Empty;
            }

            // Create trx test element from rocksteady test case
            var testElement = GetOrCreateTestElement(executionId, parentExecutionId, testType, parentTestElement, e.Result.TestCase);

            // Update test links
            UpdateTestLinks(testElement, parentTestElement);

            // Convert the rocksteady result to trx test result
            var testResult = CreateTestResult(executionId, parentExecutionId, testType, testElement, parentTestElement, parentTestResult, e.Result);

            // Update test entries
            UpdateTestEntries(executionId, parentExecutionId, testElement, parentTestElement);

            // Set various counts (passtests, failed tests, total tests)
            this.totalTests++;
            if (testResult.Outcome == TrxLoggerObjectModel.TestOutcome.Failed)
            {
                this.testRunOutcome = TrxLoggerObjectModel.TestOutcome.Failed;
                this.failTests++;
            }
            else if (testResult.Outcome == TrxLoggerObjectModel.TestOutcome.Passed)
            {
                this.passTests++;
            }
        }

        /// <summary>
        /// Called when a test run is completed.
        /// </summary>
        /// <param name="sender">
        /// The sender.
        /// </param>
        /// <param name="e">
        /// Test run complete events arguments.
        /// </param>
        public void TestRunCompleteHandler(object sender, TestRunCompleteEventArgs e)
        {
            System.Diagnostics.Debugger.Launch();
            if (this.testRun != null)
            {
                XmlPersistence helper = new XmlPersistence();
                XmlTestStoreParameters parameters = XmlTestStoreParameters.GetParameters();
                XmlElement rootElement = helper.CreateRootElement("TestRun");

                // Save runId/username/creation time etc.
                this.testRun.Finished = DateTime.Now;
                helper.SaveSingleFields(rootElement, this.testRun, parameters);

                // Save test settings
                helper.SaveObject(this.testRun.RunConfiguration, rootElement, "TestSettings", parameters);

                // Save test results
                helper.SaveIEnumerable(this.results.Values, rootElement, "Results", ".", null, parameters);

                // Save test definitions
                helper.SaveIEnumerable(this.testElements.Values, rootElement, "TestDefinitions", ".", null, parameters);

                // Save test entries
                helper.SaveIEnumerable(this.entries.Values, rootElement, "TestEntries", ".", "TestEntry", parameters);

                // Save default categories
                List<TestListCategory> categories = new List<TestListCategory>();
                categories.Add(TestListCategory.UncategorizedResults);
                categories.Add(TestListCategory.AllResults);
                helper.SaveList<TestListCategory>(categories, rootElement, "TestLists", ".", "TestList", parameters);

                // Save summary
                if (this.testRunOutcome == TrxLoggerObjectModel.TestOutcome.Passed)
                {
                    this.testRunOutcome = TrxLoggerObjectModel.TestOutcome.Completed;
                }

                List<string> errorMessages = new List<string>();
                List<CollectorDataEntry> collectorEntries = Converter.ToCollectionEntries(e.AttachmentSets, this.testRun, this.testResultsDirPath);
                IList<String> resultFiles = Converter.ToResultFiles(e.AttachmentSets, this.testRun, this.testResultsDirPath, errorMessages);

                if (errorMessages.Count > 0)
                {
                    // Got some errors while attaching files, report them and set the outcome of testrun to be Error...
                    this.testRunOutcome = TrxLoggerObjectModel.TestOutcome.Error;
                    foreach (string msg in errorMessages)
                    {
                        RunInfo runMessage = new RunInfo(msg, null, Environment.MachineName, TrxLoggerObjectModel.TestOutcome.Error);
                        this.runLevelErrorsAndWarnings.Add(runMessage);
                    }
                }

                TestRunSummary runSummary = new TestRunSummary(
                    this.totalTests,
                    this.passTests + this.failTests,
                    this.passTests,
                    this.failTests,
                    this.testRunOutcome,
                    this.runLevelErrorsAndWarnings,
                    this.runLevelStdOut.ToString(),
                    resultFiles,
                    collectorEntries);

                helper.SaveObject(runSummary, rootElement, "ResultSummary", parameters);

                //Save results to Trx file
                this.DeriveTrxFilePath();
                this.PopulateTrxFile(this.trxFilePath, rootElement);
            }
        }

        /// <summary>
        /// populate trx file from the xmlelement
        /// </summary>
        /// <param name="trxFileName">
        /// Trx full path
        /// </param>
        /// <param name="rootElement">
        /// XmlElement.
        /// </param>
        internal virtual void PopulateTrxFile(string trxFileName, XmlElement rootElement)
        {
            try
            {
                var trxFileDirPath = Path.GetDirectoryName(trxFilePath);
                if (Directory.Exists(trxFileDirPath) == false)
                {
                    Directory.CreateDirectory(trxFileDirPath);
                }

                if (File.Exists(trxFilePath))
                {
                    var overwriteWarningMsg = string.Format(CultureInfo.CurrentCulture,
                        TrxLoggerResources.TrxLoggerResultsFileOverwriteWarning, trxFileName);
                    ConsoleOutput.Instance.Warning(false, overwriteWarningMsg);
                    EqtTrace.Warning(overwriteWarningMsg);
                }

                using (var fs = File.Open(trxFileName, FileMode.Create))
                {
                    rootElement.OwnerDocument.Save(fs);
                }
                String resultsFileMessage = String.Format(CultureInfo.CurrentCulture, TrxLoggerResources.TrxLoggerResultsFile, trxFileName);
                ConsoleOutput.Instance.Information(false, resultsFileMessage);
                EqtTrace.Info(resultsFileMessage);
            }
            catch (System.UnauthorizedAccessException fileWriteException)
            {
                ConsoleOutput.Instance.Error(false, fileWriteException.Message);
            }
        }

        // Initializes trx logger cache.
        private void InitializeInternal()
        {
            this.results = new Dictionary<Guid, TrxLoggerObjectModel.ITestResult>();
            this.innerResults = new Dictionary<Guid, TrxLoggerObjectModel.ITestResult>();
            this.testElements = new Dictionary<Guid, ITestElement>();
            this.entries = new Dictionary<Guid,TestEntry>();
            this.innerTestEntries = new Dictionary<Guid, TestEntry>();
            this.runLevelErrorsAndWarnings = new List<RunInfo>();
            this.testRun = null;
            this.totalTests = 0;
            this.passTests = 0;
            this.failTests = 0;
            this.runLevelStdOut = new StringBuilder();
            this.testRunStartTime = DateTime.Now;
        }

        /// <summary>
        /// Add run level informational message
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        private void AddRunLevelInformationalMessage(string message)
        {
            this.runLevelStdOut.Append(message);
        }

        // Handle the skipped test result
        [SuppressMessage("StyleCop.CSharp.NamingRules", "SA1305:FieldNamesMustNotUseHungarianNotation", Justification = "Reviewed. Suppression is OK here.")]
        private void HandleSkippedTest(ObjectModel.TestResult rsTestResult)
        {
            Debug.Assert(rsTestResult.Outcome == ObjectModel.TestOutcome.Skipped, "Test Result should be skipped but it is " + rsTestResult.Outcome);

            ObjectModel.TestCase testCase = rsTestResult.TestCase;
            string testCaseName = !string.IsNullOrEmpty(testCase.DisplayName) ? testCase.DisplayName : testCase.FullyQualifiedName;
            string message = String.Format(CultureInfo.CurrentCulture, TrxLoggerResources.MessageForSkippedTests, testCaseName);
            this.AddRunLevelInformationalMessage(message);
        }

        private void DeriveTrxFilePath()
        {
            if (this.parametersDictionary != null)
            {
                var isLogFileNameParameterExists = this.parametersDictionary.TryGetValue(TrxLoggerConstants.LogFileNameKey, out string logFileNameValue);
                if (isLogFileNameParameterExists && !string.IsNullOrWhiteSpace(logFileNameValue))
                {
                    this.trxFilePath = Path.Combine(this.testResultsDirPath, logFileNameValue);
                }
                else
                {
                    this.SetDefaultTrxFilePath();
                }
            }
            else
            {
                this.SetDefaultTrxFilePath();
            }
        }

        /// <summary>
        /// Sets auto generated Trx file name under test results directory.
        /// </summary>
        private void SetDefaultTrxFilePath()
        {
            var defaultTrxFileName = this.testRun.RunConfiguration.RunDeploymentRootDirectory + ".trx";
            this.trxFilePath = FileHelper.GetNextIterationFileName(this.testResultsDirPath, defaultTrxFileName, false);
        }

        /// <summary>
        /// Creates test run.
        /// </summary>
        private void CreateTestRun()
        {
            // Skip run creation if already exists.
            if (testRun != null)
                return;

            Guid runId = Guid.NewGuid();
            this.testRun = new TestRun(runId);

            // We cannot rely on the StartTime for the first test result
            // In case of parallel, first test result is the fastest test and not the one which started first.
            // Setting Started to DateTime.Now in Intialize will make sure we include the startup cost, which was being ignored earlier.
            // This is in parity with the way we set this.testRun.Finished
            this.testRun.Started = this.testRunStartTime;

            // Save default test settings
            string runDeploymentRoot = FileHelper.ReplaceInvalidFileNameChars(this.testRun.Name);
            TestRunConfiguration testrunConfig = new TestRunConfiguration("default");
            testrunConfig.RunDeploymentRootDirectory = runDeploymentRoot;
            this.testRun.RunConfiguration = testrunConfig;
        }

        /// <summary>
        /// Gets test result from stored test results.
        /// </summary>
        /// <param name="executionId"></param>
        /// <returns>Test result</returns>
        private ITestResult GetTestResult(Guid executionId)
        {
            ITestResult testResult = null;

            if (executionId != Guid.Empty)
            {
                this.results.TryGetValue(executionId, out testResult);

                if (testResult == null)
                    this.innerResults.TryGetValue(executionId, out testResult);
            }

            return testResult;
        }

        /// <summary>
        /// Gets test element from stored test elements.
        /// </summary>
        /// <param name="testId"></param>
        /// <returns></returns>
        private ITestElement GetTestElement(Guid testId)
        {
            this.testElements.TryGetValue(testId, out var testElement);
            return testElement;
        }

        /// <summary>
        /// Gets or creates test element.
        /// </summary>
        /// <param name="executionId"></param>
        /// <param name="parentExecutionId"></param>
        /// <param name="testType"></param>
        /// <param name="parentTestElement"></param>
        /// <param name="rockSteadyTestCase"></param>
        /// <returns>Trx test element</returns>
        private ITestElement GetOrCreateTestElement(Guid executionId, Guid parentExecutionId, TestType testType, ITestElement parentTestElement, ObjectModel.TestCase rockSteadyTestCase)
        {
            ITestElement testElement = parentTestElement;
            if (parentTestElement == null || parentTestElement.TestType.Equals(TrxLoggerConstants.OrderedTestType))
            {
                Guid testId = Converter.GetTestId(rockSteadyTestCase);
                testElement = GetTestElement(testId);

                if (testElement == null)
                {
                    testElement = Converter.ToTestElement(testId, executionId, parentExecutionId, testType, rockSteadyTestCase);
                    testElements.Add(testId, testElement);
                }
            }

            return testElement;
        }

        /// <summary>
        /// Update test links
        /// </summary>
        /// <param name="testElement"></param>
        /// <param name="parentTestElement"></param>
        private void UpdateTestLinks(ITestElement testElement, ITestElement parentTestElement)
        {
            if (parentTestElement != null &&
                parentTestElement.TestType.Equals(TrxLoggerConstants.OrderedTestType) &&
                !(parentTestElement as OrderedTestElement).TestLinks.ContainsKey(testElement.Id.Id))
            {
                (parentTestElement as OrderedTestElement).TestLinks.Add(testElement.Id.Id, new TestLink(testElement.Id.Id, testElement.Name, testElement.Storage));
            }
        }

        /// <summary>
        /// Creates test result
        /// </summary>
        /// <param name="executionId"></param>
        /// <param name="parentExecutionId"></param>
        /// <param name="testType"></param>
        /// <param name="testElement"></param>
        /// <param name="parentTestElement"></param>
        /// <param name="parentTestResult"></param>
        /// <param name="rocksteadyTestResult"></param>
        /// <returns>Trx test result</returns>
        private ITestResult CreateTestResult(Guid executionId, Guid parentExecutionId, TestType testType, 
            ITestElement testElement, ITestElement parentTestElement, ITestResult parentTestResult, ObjectModel.TestResult rocksteadyTestResult)
        {
            // Create test result
            TrxLoggerObjectModel.TestOutcome testOutcome = Converter.ToOutcome(rocksteadyTestResult.Outcome);
            var testResult = Converter.ToTestResult(testElement.Id.Id, executionId, parentExecutionId, testElement.Name,
                this.testResultsDirPath, testType, testElement.CategoryId, testOutcome, this.testRun, rocksteadyTestResult);

            // Normal result scenario
            if (parentTestResult == null)
            {
                this.results.Add(executionId, testResult);
                return testResult;
            }

            // Ordered test inner result scenario
            if (parentTestElement != null && parentTestElement.TestType.Equals(TrxLoggerConstants.OrderedTestType))
            {
                (parentTestResult as TestResultAggregation).InnerResults.Add(testResult);
                this.innerResults.Add(executionId, testResult);
                return testResult;
            }
            
            // Data driven inner result scenario
            if (parentTestElement != null && parentTestElement.TestType.Equals(TrxLoggerConstants.UnitTestType))
            {
                (parentTestResult as TestResultAggregation).InnerResults.Add(testResult);
                testResult.DataRowInfo = (parentTestResult as TestResultAggregation).InnerResults.Count;
                testResult.ResultType = TrxLoggerConstants.InnerDataDrivenResultType;
                parentTestResult.ResultType = TrxLoggerConstants.ParentDataDrivenResultType;
                return testResult;
            }

            return testResult;
        }

        /// <summary>
        /// Update test entries
        /// </summary>
        /// <param name="executionId"></param>
        /// <param name="parentExecutionId"></param>
        /// <param name="testElement"></param>
        /// <param name="parentTestElement"></param>
        private void UpdateTestEntries(Guid executionId, Guid parentExecutionId, ITestElement testElement, ITestElement parentTestElement)
        {
            TestEntry te = new TestEntry(testElement.Id, TestListCategory.UncategorizedResults.Id);
            te.ExecutionId = executionId;

            if (parentTestElement == null)
            {
                this.entries.Add(executionId, te);
            }
            else if (parentTestElement.TestType.Equals(TrxLoggerConstants.OrderedTestType))
            {
                te.ParentExecutionId = parentExecutionId;

                var parentTestEntry = GetTestEntry(parentExecutionId);
                if (parentTestEntry != null)
                    parentTestEntry.TestEntries.Add(te);

                this.innerTestEntries.Add(executionId, te);
            }
        }

        /// <summary>
        /// Gets test entry from stored test entries.
        /// </summary>
        /// <param name="executionId"></param>
        /// <returns>Test entry</returns>
        private TestEntry GetTestEntry(Guid executionId)
        {
            TestEntry testEntry = null;

            if (executionId != Guid.Empty)
            {
                this.entries.TryGetValue(executionId, out testEntry);

                if (testEntry == null)
                    this.innerTestEntries.TryGetValue(executionId, out testEntry);
            }

            return testEntry;
        }

        #endregion
    }
}