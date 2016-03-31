﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics.Log;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics.EngineV1
{
    internal partial class DiagnosticIncrementalAnalyzer
    {
        public async override Task<ImmutableArray<DiagnosticData>> GetSpecificCachedDiagnosticsAsync(Solution solution, object id, bool includeSuppressedDiagnostics = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var diagnostics = await (new IDECachedDiagnosticGetter(this).GetSpecificDiagnosticsAsync(solution, id, cancellationToken)).ConfigureAwait(false);
            return FilterSuppressedDiagnostics(diagnostics, includeSuppressedDiagnostics);
        }

        public async override Task<ImmutableArray<DiagnosticData>> GetCachedDiagnosticsAsync(Solution solution, ProjectId projectId, DocumentId documentId, bool includeSuppressedDiagnostics = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var diagnostics = await (new IDECachedDiagnosticGetter(this).GetDiagnosticsAsync(solution, projectId, documentId, cancellationToken)).ConfigureAwait(false);
            return FilterSuppressedDiagnostics(diagnostics, includeSuppressedDiagnostics);
        }

        public async override Task<ImmutableArray<DiagnosticData>> GetSpecificDiagnosticsAsync(Solution solution, object id, bool includeSuppressedDiagnostics = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var diagnostics = await (new IDELatestDiagnosticGetter(this).GetSpecificDiagnosticsAsync(solution, id, cancellationToken)).ConfigureAwait(false);
            return FilterSuppressedDiagnostics(diagnostics, includeSuppressedDiagnostics);
        }

        public async override Task<ImmutableArray<DiagnosticData>> GetDiagnosticsAsync(Solution solution, ProjectId projectId, DocumentId documentId, bool includeSuppressedDiagnostics = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var diagnostics = await (new IDELatestDiagnosticGetter(this).GetDiagnosticsAsync(solution, projectId, documentId, cancellationToken)).ConfigureAwait(false);
            return FilterSuppressedDiagnostics(diagnostics, includeSuppressedDiagnostics);
        }

        public async override Task<ImmutableArray<DiagnosticData>> GetDiagnosticsForIdsAsync(Solution solution, ProjectId projectId, DocumentId documentId, ImmutableHashSet<string> diagnosticIds, bool includeSuppressedDiagnostics = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var diagnostics = await (new IDELatestDiagnosticGetter(this, diagnosticIds).GetDiagnosticsAsync(solution, projectId, documentId, cancellationToken)).ConfigureAwait(false);
            return FilterSuppressedDiagnostics(diagnostics, includeSuppressedDiagnostics);
        }

        public async override Task<ImmutableArray<DiagnosticData>> GetProjectDiagnosticsForIdsAsync(Solution solution, ProjectId projectId, ImmutableHashSet<string> diagnosticIds, bool includeSuppressedDiagnostics = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var diagnostics = await (new IDELatestDiagnosticGetter(this, diagnosticIds).GetProjectDiagnosticsAsync(solution, projectId, cancellationToken)).ConfigureAwait(false);
            return FilterSuppressedDiagnostics(diagnostics, includeSuppressedDiagnostics);
        }

        private static ImmutableArray<DiagnosticData> FilterSuppressedDiagnostics(ImmutableArray<DiagnosticData> diagnostics, bool includeSuppressedDiagnostics)
        {
            if (includeSuppressedDiagnostics || diagnostics.IsDefaultOrEmpty)
            {
                return diagnostics;
            }

            ImmutableArray<DiagnosticData>.Builder builder = null;
            for (int i = 0; i < diagnostics.Length; i++)
            {
                var diagnostic = diagnostics[i];
                if (diagnostic.IsSuppressed)
                {
                    if (builder == null)
                    {
                        builder = ImmutableArray.CreateBuilder<DiagnosticData>();
                        for (int j = 0; j < i; j++)
                        {
                            builder.Add(diagnostics[j]);
                        }
                    }
                }
                else if (builder != null)
                {
                    builder.Add(diagnostic);
                }
            }

            return builder != null ? builder.ToImmutable() : diagnostics;
        }

        private abstract class DiagnosticsGetter
        {
            protected readonly DiagnosticIncrementalAnalyzer Owner;
            private ImmutableArray<DiagnosticData>.Builder _builder;

            public DiagnosticsGetter(DiagnosticIncrementalAnalyzer owner)
            {
                this.Owner = owner;
                _builder = null;
            }

            protected StateManager StateManager
            {
                get { return this.Owner._stateManager; }
            }

            protected abstract Task AppendDocumentDiagnosticsOfStateTypeAsync(Document document, StateType stateType, CancellationToken cancellationToken);
            protected abstract Task AppendProjectStateDiagnosticsAsync(Project project, Document document, Func<DiagnosticData, bool> predicate, CancellationToken cancellationToken);

            public async Task<ImmutableArray<DiagnosticData>> GetDiagnosticsAsync(Solution solution, ProjectId projectId, DocumentId documentId, CancellationToken cancellationToken)
            {
                if (solution == null)
                {
                    return GetDiagnosticData();
                }

                if (documentId != null)
                {
                    var document = solution.GetDocument(documentId);

                    await AppendSyntaxAndDocumentDiagnosticsAsync(document, cancellationToken).ConfigureAwait(false);
                    await AppendProjectStateDiagnosticsAsync(document.Project, document, d => d.DocumentId == documentId, cancellationToken).ConfigureAwait(false);
                    return GetDiagnosticData();
                }

                if (projectId != null)
                {
                    await AppendDiagnosticsAsync(solution.GetProject(projectId), cancellationToken: cancellationToken).ConfigureAwait(false);
                    return GetDiagnosticData();
                }

                await AppendDiagnosticsAsync(solution, cancellationToken: cancellationToken).ConfigureAwait(false);
                return GetDiagnosticData();
            }

            public async Task<ImmutableArray<DiagnosticData>> GetProjectDiagnosticsAsync(Solution solution, ProjectId projectId, CancellationToken cancellationToken)
            {
                if (solution == null)
                {
                    return GetDiagnosticData();
                }

                if (projectId != null)
                {
                    await AppendProjectDiagnosticsAsync(solution.GetProject(projectId), cancellationToken: cancellationToken).ConfigureAwait(false);
                    return GetDiagnosticData();
                }

                await AppendProjectDiagnosticsAsync(solution, cancellationToken).ConfigureAwait(false);
                return GetDiagnosticData();
            }

            private async Task AppendProjectDiagnosticsAsync(Solution solution, CancellationToken cancellationToken)
            {
                if (solution == null)
                {
                    return;
                }

                foreach (var project in solution.Projects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AppendProjectDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);
                }
            }

            private Task AppendProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            {
                if (project == null)
                {
                    return SpecializedTasks.EmptyTask;
                }

                return AppendProjectStateDiagnosticsAsync(project, null, d => d.ProjectId == project.Id, cancellationToken);
            }

            private async Task AppendDiagnosticsAsync(Solution solution, CancellationToken cancellationToken)
            {
                if (solution == null)
                {
                    return;
                }

                foreach (var project in solution.Projects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AppendDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);
                }
            }

            private async Task AppendDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            {
                if (project == null)
                {
                    return;
                }

                await AppendProjectStateDiagnosticsAsync(project, null, d => true, cancellationToken).ConfigureAwait(false);

                var documents = project.Documents.ToImmutableArray();
                var tasks = new Task[documents.Length];
                for (int i = 0; i < documents.Length; i++)
                {
                    var document = documents[i];
                    tasks[i] = Task.Run(async () => await AppendSyntaxAndDocumentDiagnosticsAsync(document, cancellationToken).ConfigureAwait(false), cancellationToken);
                };

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            protected async Task AppendSyntaxAndDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
            {
                if (document == null)
                {
                    return;
                }

                await AppendDocumentDiagnosticsOfStateTypeAsync(document, StateType.Syntax, cancellationToken).ConfigureAwait(false);
                await AppendDocumentDiagnosticsOfStateTypeAsync(document, StateType.Document, cancellationToken).ConfigureAwait(false);
            }

            protected void AppendDiagnostics(IEnumerable<DiagnosticData> items)
            {
                if (_builder == null)
                {
                    Interlocked.CompareExchange(ref _builder, ImmutableArray.CreateBuilder<DiagnosticData>(), null);
                }

                lock (_builder)
                {
                    _builder.AddRange(items);
                }
            }

            protected virtual ImmutableArray<DiagnosticData> GetDiagnosticData()
            {
                return _builder != null ? _builder.ToImmutableArray() : ImmutableArray<DiagnosticData>.Empty;
            }

            protected static ProjectId GetProjectId(object key)
            {
                var documentId = key as DocumentId;
                if (documentId != null)
                {
                    return documentId.ProjectId;
                }

                var projectId = key as ProjectId;
                if (projectId != null)
                {
                    return projectId;
                }

                return Contract.FailWithReturn<ProjectId>("Shouldn't reach here");
            }

            protected static object GetProjectOrDocument(Solution solution, object key)
            {
                var documentId = key as DocumentId;
                if (documentId != null)
                {
                    return solution.GetDocument(documentId);
                }

                var projectId = key as ProjectId;
                if (projectId != null)
                {
                    return solution.GetProject(projectId);
                }

                return Contract.FailWithReturn<object>("Shouldn't reach here");
            }
        }

        private class IDECachedDiagnosticGetter : DiagnosticsGetter
        {
            public IDECachedDiagnosticGetter(DiagnosticIncrementalAnalyzer owner) : base(owner)
            {
            }

            public async Task<ImmutableArray<DiagnosticData>> GetSpecificDiagnosticsAsync(Solution solution, object id, CancellationToken cancellationToken)
            {
                if (solution == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var key = id as ArgumentKey;
                if (key == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var projectId = GetProjectId(key.Key);
                var project = solution.GetProject(projectId);

                // when we return cached result, make sure we at least return something that exist in current solution
                if (project == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var stateSet = this.StateManager.GetOrCreateStateSet(project, key.Analyzer);
                if (stateSet == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var state = stateSet.GetState(key.StateType);
                if (state == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                // for now, it just use wait and get result
                var documentOrProject = GetProjectOrDocument(solution, key.Key);
                if (documentOrProject == null)
                {
                    // Document or project might have been removed from the solution.
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var existingData = await state.TryGetExistingDataAsync(documentOrProject, cancellationToken).ConfigureAwait(false);
                if (existingData == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                return existingData.Items;
            }

            protected override async Task AppendDocumentDiagnosticsOfStateTypeAsync(Document document, StateType stateType, CancellationToken cancellationToken)
            {
                foreach (var stateSet in this.StateManager.GetStateSets(document.Project))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var state = stateSet.GetState(stateType);

                    var existingData = await state.TryGetExistingDataAsync(document, cancellationToken).ConfigureAwait(false);
                    if (existingData == null || existingData.Items.Length == 0)
                    {
                        continue;
                    }

                    AppendDiagnostics(existingData.Items);
                }
            }

            protected override async Task AppendProjectStateDiagnosticsAsync(
                Project project, Document document, Func<DiagnosticData, bool> predicate, CancellationToken cancellationToken)
            {
                var documents = document == null ? project.Documents.ToList() : SpecializedCollections.SingletonEnumerable(document);

                foreach (var stateSet in this.StateManager.GetStateSets(project))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var state = stateSet.GetState(StateType.Project);

                    await AppendExistingDiagnosticsFromStateAsync(state, project, predicate, cancellationToken).ConfigureAwait(false);

                    foreach (var current in documents)
                    {
                        await AppendExistingDiagnosticsFromStateAsync(state, current, predicate, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            private async Task AppendExistingDiagnosticsFromStateAsync(DiagnosticState state, object documentOrProject, Func<DiagnosticData, bool> predicate, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var existingData = await state.TryGetExistingDataAsync(documentOrProject, cancellationToken).ConfigureAwait(false);
                if (existingData == null || existingData.Items.Length == 0)
                {
                    return;
                }

                AppendDiagnostics(existingData.Items.Where(predicate));
            }
        }

        private abstract class LatestDiagnosticsGetter : DiagnosticsGetter
        {
            protected readonly ImmutableHashSet<string> DiagnosticIds;
            private readonly Dictionary<ProjectId, CompilationWithAnalyzers> _compilationWithAnalyzersMap;

            public LatestDiagnosticsGetter(DiagnosticIncrementalAnalyzer owner, ImmutableHashSet<string> diagnosticIds) : base(owner)
            {
                this.DiagnosticIds = diagnosticIds;
                _compilationWithAnalyzersMap = new Dictionary<ProjectId, CompilationWithAnalyzers>();
            }

            protected abstract Task<AnalysisData> GetDiagnosticAnalysisDataAsync(Solution solution, DiagnosticAnalyzerDriver analyzerDriver, StateSet stateSet, StateType stateType, VersionArgument versions);
            protected abstract void FilterDiagnostics(AnalysisData analysisData, Func<DiagnosticData, bool> predicateOpt = null);

            protected override Task AppendDocumentDiagnosticsOfStateTypeAsync(Document document, StateType stateType, CancellationToken cancellationToken)
            {
                return AppendDiagnosticsOfStateTypeAsync(document, stateType, d => true, cancellationToken);
            }

            protected override Task AppendProjectStateDiagnosticsAsync(Project project, Document document, Func<DiagnosticData, bool> predicate, CancellationToken cancellationToken)
            {
                return AppendDiagnosticsOfStateTypeAsync(project, StateType.Project, predicate, cancellationToken);
            }

            protected async Task<DiagnosticAnalyzerDriver> GetDiagnosticAnalyzerDriverAsync(object documentOrProject, StateType stateType, CancellationToken cancellationToken)
            {
                // We can run analysis concurrently for explicit diagnostic requests.
                const bool concurrentAnalysis = true;

                // We need to compute suppressed diagnostics - diagnostic clients may or may not request for suppressed diagnostics.
                const bool reportSuppressedDiagnostics = true;

                var document = documentOrProject as Document;
                if (document != null)
                {
                    Contract.Requires(stateType != StateType.Project);
                    var compilationWithAnalyzersOpt = await GetCompilationWithAnalyzersAsync(document.Project, concurrentAnalysis, reportSuppressedDiagnostics, cancellationToken).ConfigureAwait(false);
                    var root = document.SupportsSyntaxTree ? await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) : null;
                    return new DiagnosticAnalyzerDriver(document, root?.FullSpan, root, Owner, concurrentAnalysis, reportSuppressedDiagnostics, compilationWithAnalyzersOpt, cancellationToken);
                }

                var project = documentOrProject as Project;
                if (project != null)
                {
                    Contract.Requires(stateType == StateType.Project);
                    var compilationWithAnalyzersOpt = await GetCompilationWithAnalyzersAsync(project, concurrentAnalysis, reportSuppressedDiagnostics, cancellationToken).ConfigureAwait(false);
                    return new DiagnosticAnalyzerDriver(project, Owner, concurrentAnalysis, reportSuppressedDiagnostics, compilationWithAnalyzersOpt, cancellationToken);
                }

                return Contract.FailWithReturn<DiagnosticAnalyzerDriver>("Can't reach here");
            }

            private async Task<CompilationWithAnalyzers> GetCompilationWithAnalyzersAsync(Project project, bool concurrentAnalysis, bool reportSuppressedDiagnostics, CancellationToken cancellationToken)
            {
                CompilationWithAnalyzers compilationWithAnalyzers;
                lock (_compilationWithAnalyzersMap)
                {
                    if (_compilationWithAnalyzersMap.TryGetValue(project.Id, out compilationWithAnalyzers))
                    {
                        return compilationWithAnalyzers;
                    }
                }

                if (project.SupportsCompilation)
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                    compilationWithAnalyzers = Owner.GetCompilationWithAnalyzers(project, compilation, concurrentAnalysis, reportSuppressedDiagnostics);
                }
                else
                {
                    compilationWithAnalyzers = null;
                }

                lock (_compilationWithAnalyzersMap)
                {
                    _compilationWithAnalyzersMap[project.Id] = compilationWithAnalyzers;
                }

                return compilationWithAnalyzers;
            }

            protected async Task<VersionArgument> GetVersionsAsync(object documentOrProject, StateType stateType, CancellationToken cancellationToken)
            {
                switch (stateType)
                {
                    case StateType.Syntax:
                        {
                            var document = (Document)documentOrProject;
                            var textVersion = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
                            var syntaxVersion = await document.GetSyntaxVersionAsync(cancellationToken).ConfigureAwait(false);
                            return new VersionArgument(textVersion, syntaxVersion);
                        }

                    case StateType.Document:
                        {
                            var document = (Document)documentOrProject;
                            var textVersion = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
                            var semanticVersion = await document.Project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);
                            return new VersionArgument(textVersion, semanticVersion);
                        }

                    case StateType.Project:
                        {
                            var project = (Project)documentOrProject;
                            var projectTextVersion = await project.GetLatestDocumentVersionAsync(cancellationToken).ConfigureAwait(false);
                            var semanticVersion = await project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);
                            var projectVersion = await project.GetDependentVersionAsync(cancellationToken).ConfigureAwait(false);
                            return new VersionArgument(projectTextVersion, semanticVersion, projectVersion);
                        }

                    default:
                        return Contract.FailWithReturn<VersionArgument>("Can't reach here");
                }
            }

            private async Task AppendDiagnosticsOfStateTypeAsync(object documentOrProject, StateType stateType, Func<DiagnosticData, bool> predicateOpt, CancellationToken cancellationToken)
            {
                Contract.ThrowIfNull(documentOrProject);
                var project = GetProject(documentOrProject);
                var solution = project.Solution;

                var driver = await GetDiagnosticAnalyzerDriverAsync(documentOrProject, stateType, cancellationToken).ConfigureAwait(false);
                var versions = await GetVersionsAsync(documentOrProject, stateType, cancellationToken).ConfigureAwait(false);

                foreach (var stateSet in this.StateManager.GetOrCreateStateSets(project))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Owner.Owner.IsAnalyzerSuppressed(stateSet.Analyzer, project) ||
                        !this.Owner.ShouldRunAnalyzerForStateType(stateSet.Analyzer, stateType, this.DiagnosticIds))
                    {
                        continue;
                    }

                    var analysisData = await GetDiagnosticAnalysisDataAsync(solution, driver, stateSet, stateType, versions).ConfigureAwait(false);
                    FilterDiagnostics(analysisData, predicateOpt);
                }
            }

            protected Project GetProject(object documentOrProject)
            {
                var document = documentOrProject as Document;
                if (document != null)
                {
                    return document.Project;
                }

                return (Project)documentOrProject;
            }

            private DiagnosticLogAggregator DiagnosticLogAggregator
            {
                get { return this.Owner.DiagnosticLogAggregator; }
            }
        }

        private class IDELatestDiagnosticGetter : LatestDiagnosticsGetter
        {
            public IDELatestDiagnosticGetter(DiagnosticIncrementalAnalyzer owner) : this(owner, null)
            {
            }

            public IDELatestDiagnosticGetter(DiagnosticIncrementalAnalyzer owner, ImmutableHashSet<string> diagnosticIds) : base(owner, diagnosticIds)
            {
            }

            public async Task<ImmutableArray<DiagnosticData>> GetSpecificDiagnosticsAsync(Solution solution, object id, CancellationToken cancellationToken)
            {
                if (solution == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var key = id as ArgumentKey;
                if (key == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var documentOrProject = GetProjectOrDocument(solution, key.Key);
                if (documentOrProject == null)
                {
                    // Document or project might have been removed from the solution.
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                if (key.StateType != StateType.Project)
                {
                    return await GetDiagnosticsAsync(documentOrProject, key, cancellationToken).ConfigureAwait(false);
                }

                return await GetDiagnosticsAsync(GetProject(documentOrProject), key, cancellationToken).ConfigureAwait(false);
            }

            private async Task<ImmutableArray<DiagnosticData>> GetDiagnosticsAsync(object documentOrProject, ArgumentKey key, CancellationToken cancellationToken)
            {
                var driver = await GetDiagnosticAnalyzerDriverAsync(documentOrProject, key.StateType, cancellationToken).ConfigureAwait(false);
                var versions = await GetVersionsAsync(documentOrProject, key.StateType, cancellationToken).ConfigureAwait(false);

                var project = GetProject(documentOrProject);
                var stateSet = this.StateManager.GetOrCreateStateSet(project, key.Analyzer);
                if (stateSet == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                var analysisData = await GetDiagnosticAnalysisDataAsync(project.Solution, driver, stateSet, key.StateType, versions).ConfigureAwait(false);
                if (key.StateType != StateType.Project)
                {
                    return analysisData.Items;
                }

                return analysisData.Items.Where(d =>
                {
                    if (key.Key is DocumentId)
                    {
                        return object.Equals(d.DocumentId, key.Key);
                    }

                    if (key.Key is ProjectId)
                    {
                        return object.Equals(d.ProjectId, key.Key);
                    }

                    return false;
                }).ToImmutableArray();
            }

            protected override void FilterDiagnostics(AnalysisData analysisData, Func<DiagnosticData, bool> predicateOpt)
            {
                if (predicateOpt == null)
                {
                    AppendDiagnostics(analysisData.Items.Where(d => this.DiagnosticIds == null || this.DiagnosticIds.Contains(d.Id)));
                    return;
                }

                AppendDiagnostics(analysisData.Items.Where(d => this.DiagnosticIds == null || this.DiagnosticIds.Contains(d.Id)).Where(predicateOpt));
            }

            protected override Task<AnalysisData> GetDiagnosticAnalysisDataAsync(
                Solution solution, DiagnosticAnalyzerDriver analyzerDriver, StateSet stateSet, StateType stateType, VersionArgument versions)
            {
                switch (stateType)
                {
                    case StateType.Syntax:
                        return this.AnalyzerExecutor.GetSyntaxAnalysisDataAsync(analyzerDriver, stateSet, versions);
                    case StateType.Document:
                        return this.AnalyzerExecutor.GetDocumentAnalysisDataAsync(analyzerDriver, stateSet, versions);
                    case StateType.Project:
                        return this.AnalyzerExecutor.GetProjectAnalysisDataAsync(analyzerDriver, stateSet, versions);
                    default:
                        return Contract.FailWithReturn<Task<AnalysisData>>("Can't reach here");
                }
            }

            protected AnalyzerExecutor AnalyzerExecutor
            {
                get { return this.Owner._executor; }
            }
        }
    }
}