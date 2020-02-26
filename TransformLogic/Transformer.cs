﻿using CxRestClient;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Text;

namespace CxAnalytics.TransformLogic
{
    /// <summary>
    /// A class that implements the data transformation.
    /// </summary>
    public class Transformer
    {
        private static ILog _log = LogManager.GetLogger(typeof(Transformer));

        private static readonly String SAST_PRODUCT_STRING = "SAST";
        private static readonly String SCA_PRODUCT_STRING = "SCA";

        private static readonly String DATE_FORMAT = "yyyy-MM-ddTHH:mm:ss.fffzzz";
        private static readonly Dictionary<String, Action<ScanDescriptor, Transformer>> _mapActions;

        static Transformer()
        {
            _mapActions = new Dictionary<string, Action<ScanDescriptor, Transformer>>()
            {
                {SAST_PRODUCT_STRING,  SastReportOutput},
                {SCA_PRODUCT_STRING,  ScaReportOutput}
            };
        }

        public static void SastReportOutput (ScanDescriptor scan, Transformer inst)
        {

            _log.Debug($"Retrieving XML Report for scan {scan.ScanId}");
            var report = CxSastXmlReport.GetXmlReport(inst.RestContext, inst.CancelToken, scan.ScanId);
            _log.Debug($"XML Report for scan {scan.ScanId} retrieved.");

            _log.Debug($"Processing XML report for scan {scan.ScanId}");
            ProcessSASTReport(scan, report, inst.ScanDetailOut);
            _log.Debug($"XML Report for scan {scan.ScanId} processed.");

            inst.OutputSASTScanSummary(scan);
        }

        public static void ScaReportOutput(ScanDescriptor sd, Transformer inst)
        {
            _log.Debug($"*********SCA ACTION FOR SCAN {sd.ScanId}******");
        }

        private Transformer(CxRestContext ctx, CancellationToken token,
            String previousStatePath)
        {
            RestContext = ctx;
            CancelToken = token;

            Policies = null;

            // Policies may not have data if M&O is not installed.
            try
            {
                Policies = new ProjectPolicyIndex(CxMnoPolicies.GetAllPolicies(ctx, token));
            }
            catch (Exception ex)
            {
                _log.Warn("Policy data is not available.", ex);
            }


            // Populate the data resolver with teams and presets
            DataResolver dr = new DataResolver();

            var presetEnum = CxPresets.GetPresets(RestContext, CancelToken);

            foreach (var preset in presetEnum)
                dr.addPreset(preset.PresetId, preset.PresetName);

            var teamEnum = CxTeams.GetTeams(RestContext, CancelToken);

            foreach (var team in teamEnum)
                dr.addTeam(team.TeamId, team.TeamName);

            // Now populate the project resolver with the projects
            ProjectResolver pr = dr.Resolve(previousStatePath);

            var projects = CxProjects.GetProjects(RestContext, CancelToken);

            foreach (var p in projects)
            {
                IEnumerable<int> projectPolicyList = CxMnoPolicies.GetPolicyIdsForProject
                    (ctx, token, p.ProjectId);

                String combinedPolicyNames = String.Empty;

                if (Policies != null)
                {
                    Policies.CorrelateProjectToPolicies(p.ProjectId, projectPolicyList);
                    combinedPolicyNames = GetFlatPolicyNames(Policies, projectPolicyList);
                }

                pr.AddProject(p.TeamId, p.PresetId, p.ProjectId, p.ProjectName, combinedPolicyNames);
            }

            // Resolve projects to get the scan resolver.
            ScanResolver sr = pr.Resolve(_mapActions);

            var sastScans = CxSastScans.GetScans(RestContext, CancelToken, CxSastScans.ScanStatus.Finished);
            foreach (var sastScan in sastScans)
            {
                sr.addScan(sastScan.ProjectId, sastScan.ScanType, SAST_PRODUCT_STRING,
                    sastScan.ScanId, sastScan.FinishTime);

                SastScanCache.Add(sastScan.ScanId, sastScan);
            }


            foreach (var p in projects)
            {
                var scaScans = CxScaScans.GetScans(ctx, token, p.ProjectId);
                foreach (var scaScan in scaScans)
                {
                    sr.addScan(scaScan.ProjectId, "Composition", SCA_PRODUCT_STRING, scaScan.ScanId,
                        scaScan.FinishTime);
                    ScaScanCache.Add(scaScan.ScanId, scaScan);
                }
            }

            ScanDescriptors = sr.Resolve(CheckTime);
        }

        private IEnumerable<ScanDescriptor> ScanDescriptors { get; set; }

        private Dictionary<String, CxSastScans.Scan> SastScanCache { get; set; }
            = new Dictionary<string, CxSastScans.Scan>();
        private Dictionary<String, CxScaScans.Scan> ScaScanCache { get; set; }
            = new Dictionary<string, CxScaScans.Scan>();

        private ProjectPolicyIndex Policies { get; set; }
        private DateTime CheckTime { get; set; } = DateTime.Now;

        private CxRestContext RestContext { get; set; }
        private CancellationToken CancelToken { get; set; }

        ParallelOptions ThreadOpts { get; set; }

        public IOutput ProjectInfoOut { get; internal set; }
        public IOutput ScanSummaryOut { get; internal set; }
        public IOutput ScanDetailOut { get; internal set; }
        public IOutput PolicyViolationDetailOut { get; internal set; }

        private ConcurrentDictionary<int, ViolatedPolicyCollection> PolicyViolations { get; set; } =
            new ConcurrentDictionary<int, ViolatedPolicyCollection>();

        private void ExecuteSweep()
        {
            // Lookup policy violations, report the project information records.
            Parallel.ForEach<ScanDescriptor>(ScanDescriptors, ThreadOpts,
            (scan) =>
            {
                if (PolicyViolations.TryAdd(scan.Project.ProjectId,
                new ViolatedPolicyCollection()))
                {
                    if (Policies != null)
                        try
                        {
                            // Collect policy violations, only once per project
                            PolicyViolations[scan.Project.ProjectId] = CxMnoRetreivePolicyViolations.
                            GetViolations(RestContext, CancelToken, scan.Project.ProjectId, Policies);
                        }
                        catch (Exception ex)
                        {
                            _log.Debug($"Policy violations for project {scan.Project.ProjectId}: " +
                            $"{scan.Project.ProjectName} are unavailable.", ex);
                        }

                    OutputProjectInfoRecords(scan, ProjectInfoOut);
                }

                // Increment the policy violation stats for each scan.
                scan.IncrementPolicyViolations(PolicyViolations[scan.Project.ProjectId].
                GetViolatedRulesByScanId(scan.ScanId));

                // Does something appropriate for the type of scan in the scan descriptor.
                scan.MapAction(scan, this);

                OutputPolicyViolationDetails(scan);
            });


        }



        /// <summary>
        /// The main logic for invoking a transformation.  It does not return until a sweep
        /// for new scans is performed across all projects.
        /// </summary>
        /// <param name="concurrentThreads">The number of concurrent scan transformation threads.</param>
        /// <param name="previousStatePath">A folder path where files will be created to store any state
        /// data required to resume operations across program runs.</param>
        /// <param name="ctx"></param>
        /// <param name="outFactory">The factory implementation for making IOutput instances
        /// used for outputting various record types.</param>
        /// <param name="records">The names of the supported record types that will be used by 
        /// the IOutputFactory to create the correct output implementation instance.</param>
        /// <param name="token">A cancellation token that can be used to stop processing of data if
        /// the task needs to be interrupted.</param>
        public static void DoTransform(int concurrentThreads, String previousStatePath,
        CxRestContext ctx, IOutputFactory outFactory, RecordNames records, CancellationToken token)
        {

            Transformer xform = new Transformer(ctx, token, previousStatePath)
            {
                ThreadOpts = new ParallelOptions()
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = concurrentThreads
                },
                ProjectInfoOut = outFactory.newInstance(records.ProjectInfo),
                ScanSummaryOut = outFactory.newInstance(records.SASTScanSummary),
                ScanDetailOut = outFactory.newInstance(records.SASTScanDetail),
                PolicyViolationDetailOut = outFactory.newInstance(records.PolicyViolations)

            };

            xform.ExecuteSweep();
        }

        private void OutputPolicyViolationDetails(ScanDescriptor scan)
        {
            SortedDictionary<String, String> header = new SortedDictionary<string, string>();
            AddPrimaryKeyElements(scan, header);
            header.Add(PropertyKeys.KEY_SCANID, scan.ScanId);
            header.Add(PropertyKeys.KEY_SCANPRODUCT, scan.ScanProduct);
            header.Add(PropertyKeys.KEY_SCANTYPE, scan.ScanType);

            var violatedRules = PolicyViolations[scan.Project.ProjectId].
                GetViolatedRulesByScanId(scan.ScanId);

            if (violatedRules != null)
                foreach (var rule in violatedRules)
                {
                    SortedDictionary<String, String> flat = new SortedDictionary<string, string>(header);
                    flat.Add("PolicyId", Convert.ToString(rule.PolicyId));
                    flat.Add("PolicyName", Policies.GetPolicyById(rule.PolicyId).Name);
                    flat.Add("RuleId", Convert.ToString(rule.RuleId));
                    flat.Add("RuleName", rule.Name);
                    flat.Add("RuleDescription", rule.Description);
                    flat.Add("RuleType", rule.RuleType);
                    flat.Add("RuleCreateDate", rule.CreatedOn.ToString(DATE_FORMAT));
                    flat.Add("FirstViolationDetectionDate", rule.FirstDetectionDate.ToString(DATE_FORMAT));
                    flat.Add("ViolationName", rule.ViolationName);
                    if (rule.ViolationOccured.HasValue)
                        flat.Add("ViolationOccurredDate", rule.ViolationOccured.Value.ToString(DATE_FORMAT));
                    if (rule.ViolationRiskScore.HasValue)
                        flat.Add("ViolationRiskScore", Convert.ToString(rule.ViolationRiskScore.Value));
                    flat.Add("ViolationSeverity", rule.ViolationSeverity);
                    if (rule.ViolationSource != null)
                        flat.Add("ViolationSource", rule.ViolationSource);
                    flat.Add("ViolationState", rule.ViolationState);
                    flat.Add("ViolationStatus", rule.ViolationStatus);
                    if (rule.ViolationType != null)
                        flat.Add("ViolationType", rule.ViolationType);

                    PolicyViolationDetailOut.write(flat);
                }
        }

        private static void ProcessSASTReport(ScanDescriptor scan, Stream report,
            IOutput scanDetailOut)
        {
            SortedDictionary<String, String> reportRec =
                new SortedDictionary<string, string>();
            AddPrimaryKeyElements(scan, reportRec);
            reportRec.Add(PropertyKeys.KEY_SCANID, scan.ScanId);
            reportRec.Add(PropertyKeys.KEY_SCANPRODUCT, scan.ScanProduct);
            reportRec.Add(PropertyKeys.KEY_SCANTYPE, scan.ScanType);

            SortedDictionary<String, String> curResultRec = null;
            SortedDictionary<String, String> curQueryRec = null;
            SortedDictionary<String, String> curPath = null;
            SortedDictionary<String, String> curPathNode = null;
            bool inSnippet = false;

            using (XmlReader xr = XmlReader.Create(report))
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        if (xr.Name.CompareTo("CxXMLResults") == 0)
                        {
                            _log.Debug($"[Scan: {scan.ScanId}] Processing attributes in CxXMLResults.");

                            scan.Preset = xr.GetAttribute("Preset");
                            scan.Initiator = xr.GetAttribute("InitiatorName");
                            scan.DeepLink = xr.GetAttribute("DeepLink");
                            scan.ScanTime = xr.GetAttribute("ScanTime");
                            scan.ReportCreateTime = DateTime.Parse(xr.GetAttribute
                                ("ReportCreationTime"));
                            scan.Comments = xr.GetAttribute("ScanComments");
                            scan.SourceOrigin = xr.GetAttribute("SourceOrigin");
                            continue;
                        }

                        if (xr.Name.CompareTo("Query") == 0)
                        {
                            _log.Debug($"[Scan: {scan.ScanId}] Processing attributes in Query " +
                                $"[{xr.GetAttribute("id")} - {xr.GetAttribute("name")}].");

                            curQueryRec = new SortedDictionary<string, string>
                                (reportRec);

                            curQueryRec.Add("QueryCategories", xr.GetAttribute("categories"));
                            curQueryRec.Add("QueryId", xr.GetAttribute("id"));
                            curQueryRec.Add("QueryCweId", xr.GetAttribute("cweId"));
                            curQueryRec.Add("QueryName", xr.GetAttribute("name"));
                            curQueryRec.Add("QueryGroup", xr.GetAttribute("group"));
                            curQueryRec.Add("QuerySeverity", xr.GetAttribute("Severity"));
                            curQueryRec.Add("QueryLanguage", xr.GetAttribute("Language"));
                            curQueryRec.Add("QueryVersionCode", xr.GetAttribute("QueryVersionCode"));
                            continue;
                        }

                        if (xr.Name.CompareTo("Result") == 0)
                        {
                            _log.Debug($"[Scan: {scan.ScanId}] Processing attributes in Result " +
                                $"[{xr.GetAttribute("NodeId")}].");

                            scan.IncrementSeverity(xr.GetAttribute("Severity"));

                            curResultRec = new SortedDictionary<string, string>(curQueryRec);
                            curResultRec.Add("VulnerabilityId", xr.GetAttribute("NodeId"));
                            curResultRec.Add("SinkFileName", xr.GetAttribute("FileName"));
                            curResultRec.Add("Status", xr.GetAttribute("Status"));
                            curResultRec.Add("SinkLine", xr.GetAttribute("Line"));
                            curResultRec.Add("SinkColumn", xr.GetAttribute("Column"));
                            curResultRec.Add("FalsePositive", xr.GetAttribute("FalsePositive"));
                            curResultRec.Add("ResultSeverity", xr.GetAttribute("Severity"));
                            // TODO: Translate state number to an appropriate string
                            curResultRec.Add("State", xr.GetAttribute("state"));
                            curResultRec.Add("Remark", xr.GetAttribute("Remark"));
                            curResultRec.Add("ResultDeepLink", xr.GetAttribute("DeepLink"));
                            continue;
                        }

                        if (xr.Name.CompareTo("Path") == 0)
                        {
                            curPath = new SortedDictionary<string, string>(curResultRec);
                            curPath.Add("ResultId", xr.GetAttribute("ResultId"));
                            curPath.Add("PathId", xr.GetAttribute("PathId"));
                            curPath.Add("SimilarityId", xr.GetAttribute("SimilarityId"));
                            continue;
                        }

                        if (xr.Name.CompareTo("PathNode") == 0)
                        {
                            curPathNode = new SortedDictionary<string, string>(curPath);
                            continue;
                        }

                        if (xr.Name.CompareTo("FileName") == 0 && curPathNode != null)
                        {
                            curPathNode.Add("NodeFileName", xr.ReadElementContentAsString());
                            continue;
                        }

                        if (xr.Name.CompareTo("Line") == 0 && curPathNode != null && !inSnippet)
                        {
                            curPathNode.Add("NodeLine", xr.ReadElementContentAsString());
                            continue;
                        }

                        if (xr.Name.CompareTo("Column") == 0 && curPathNode != null)
                        {
                            curPathNode.Add("NodeColumn", xr.ReadElementContentAsString());
                            continue;
                        }

                        if (xr.Name.CompareTo("NodeId") == 0 && curPathNode != null)
                        {
                            curPathNode.Add("NodeId", xr.ReadElementContentAsString());
                            continue;
                        }

                        if (xr.Name.CompareTo("Name") == 0 && curPathNode != null)
                        {
                            curPathNode.Add("NodeName", xr.ReadElementContentAsString());
                            continue;
                        }

                        if (xr.Name.CompareTo("Type") == 0 && curPathNode != null)
                        {
                            curPathNode.Add("NodeType", xr.ReadElementContentAsString());
                            continue;
                        }

                        if (xr.Name.CompareTo("Length") == 0 && curPathNode != null)
                        {
                            curPathNode.Add("NodeLength", xr.ReadElementContentAsString());
                            continue;
                        }

                        if (xr.Name.CompareTo("Snippet") == 0 && curPathNode != null)
                        {
                            inSnippet = true;
                            continue;
                        }

                        if (xr.Name.CompareTo("Code") == 0 && curPathNode != null)
                        {
                            curPathNode.Add("NodeCodeSnippet", xr.ReadElementContentAsString());
                            continue;
                        }
                    }


                    if (xr.NodeType == XmlNodeType.EndElement)
                    {
                        if (xr.Name.CompareTo("CxXMLResults") == 0)
                        {
                            _log.Debug($"[Scan: {scan.ScanId}] Finished processing CxXMLResults");
                            continue;
                        }

                        if (xr.Name.CompareTo("Query") == 0)
                        {
                            curQueryRec = null;
                            continue;
                        }

                        if (xr.Name.CompareTo("Result") == 0)
                        {
                            curResultRec = null;
                            continue;
                        }

                        if (xr.Name.CompareTo("Path") == 0)
                        {
                            curPath = null;
                            continue;
                        }

                        if (xr.Name.CompareTo("PathNode") == 0)
                        {
                            scanDetailOut.write(curPathNode);
                            curPathNode = null;
                            continue;
                        }

                        if (xr.Name.CompareTo("PathNode") == 0)
                        {
                            inSnippet = false;
                            continue;
                        }
                    }
                }
        }

        private void OutputSASTScanSummary(ScanDescriptor scanRecord)
        {
            if (ScanSummaryOut == null)
                return;

            SortedDictionary<String, String> flat = new SortedDictionary<string, string>();
            AddPrimaryKeyElements(scanRecord, flat);
            flat.Add(PropertyKeys.KEY_SCANID, scanRecord.ScanId);
            flat.Add(PropertyKeys.KEY_SCANPRODUCT, scanRecord.ScanProduct);
            flat.Add(PropertyKeys.KEY_SCANTYPE, scanRecord.ScanType);
            flat.Add(PropertyKeys.KEY_SCANFINISH, scanRecord.FinishedStamp.ToString(DATE_FORMAT));
            flat.Add(PropertyKeys.KEY_SCANSTART, SastScanCache[scanRecord.ScanId].StartTime.ToString(DATE_FORMAT));
            flat.Add(PropertyKeys.KEY_SCANRISK, SastScanCache[scanRecord.ScanId].ScanRisk.ToString());
            flat.Add(PropertyKeys.KEY_SCANRISKSEV, SastScanCache[scanRecord.ScanId].ScanRiskSeverity.ToString());
            flat.Add(PropertyKeys.KEY_LOC, SastScanCache[scanRecord.ScanId].LinesOfCode.ToString());
            flat.Add(PropertyKeys.KEY_FLOC, SastScanCache[scanRecord.ScanId].FailedLinesOfCode.ToString());
            flat.Add(PropertyKeys.KEY_FILECOUNT, SastScanCache[scanRecord.ScanId].FileCount.ToString());
            flat.Add(PropertyKeys.KEY_VERSION, SastScanCache[scanRecord.ScanId].CxVersion);
            flat.Add(PropertyKeys.KEY_LANGS, SastScanCache[scanRecord.ScanId].Languages);
            flat.Add(PropertyKeys.KEY_PRESET, scanRecord.Preset);
            flat.Add("Initiator", scanRecord.Initiator);
            flat.Add("DeepLink", scanRecord.DeepLink);
            flat.Add("ScanTime", scanRecord.ScanTime);
            flat.Add("ReportCreationTime", scanRecord.ReportCreateTime.ToString(DATE_FORMAT));
            flat.Add("ScanComments", scanRecord.Comments);
            flat.Add("SourceOrigin", scanRecord.SourceOrigin);
            foreach (var sev in scanRecord.SeverityCounts.Keys)
                flat.Add(sev, Convert.ToString(scanRecord.SeverityCounts[sev]));

            if (scanRecord.HasPoliciesApplied)
            {
                flat.Add("PoliciesViolated", Convert.ToString(scanRecord.PoliciesViolated));
                flat.Add("RulesViolated", Convert.ToString(scanRecord.RulesViolated));
                flat.Add("PolicyViolations", Convert.ToString(scanRecord.Violations));
            }

            ScanSummaryOut.write(flat);
        }

        private static void OutputProjectInfoRecords(ScanDescriptor scanRecord,
            IOutput project_info_out)
        {
            SortedDictionary<String, String> flat = new SortedDictionary<string, string>();
            AddPrimaryKeyElements(scanRecord, flat);

            flat.Add(PropertyKeys.KEY_PRESET, scanRecord.Project.PresetName);
            flat.Add("Policies", scanRecord.Project.Policies);

            foreach (var lastScanProduct in scanRecord.Project.LatestScanDateByProduct.Keys)
                flat.Add($"{lastScanProduct}_LastScanDate",
                    scanRecord.Project.LatestScanDateByProduct[lastScanProduct].ToString(DATE_FORMAT));

            foreach (var scanCountProduct in scanRecord.Project.ScanCountByProduct.Keys)
                flat.Add($"{scanCountProduct}_Scans",
                    scanRecord.Project.ScanCountByProduct[scanCountProduct].ToString());

            project_info_out.write(flat);

        }

        private static void AddPrimaryKeyElements(ScanDescriptor rec, IDictionary<string, string> flat)
        {
            flat.Add(PropertyKeys.KEY_PROJECTID, rec.Project.ProjectId.ToString());
            flat.Add(PropertyKeys.KEY_PROJECTNAME, rec.Project.ProjectName);
            flat.Add(PropertyKeys.KEY_TEAMNAME, rec.Project.TeamName);
        }

        private static String GetFlatPolicyNames(PolicyCollection policies,
            IEnumerable<int> policyIds)
        {
            StringBuilder b = new StringBuilder();

            foreach (var pid in policyIds)
            {
                if (b.Length > 0)
                    b.Append(';');

                b.Append(policies.GetPolicyById(pid).Name);
            }

            return b.ToString();
        }
    }
}
