﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using MirrorSharp.Advanced;
using MirrorSharp.Internal.Languages;
using MirrorSharp.Internal.Reflection;

namespace MirrorSharp.Internal {
    internal class WorkSession : IWorkSession {
        private static readonly TextChange[] NoTextChanges = new TextChange[0];

        [CanBeNull] private readonly IWorkSessionOptions _options;
        [NotNull] private ILanguage _language;
        [NotNull] private IDictionary<string, Func<ParseOptions, ParseOptions>> _parseOptionsChanges = new Dictionary<string, Func<ParseOptions, ParseOptions>>();
        [NotNull] private IDictionary<string, Func<CompilationOptions, CompilationOptions>> _compilationOptionsChanges = new Dictionary<string, Func<CompilationOptions, CompilationOptions>>();

        [CanBeNull] private CustomWorkspace _workspace;

        private SourceText _sourceText;
        private bool _documentOutOfDate;
        private Document _document;

        private Completion _completion;
        private ImmutableArray<DiagnosticAnalyzer> _analyzers;
        private ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> _codeFixProviders;
        private ImmutableArray<ISignatureHelpProviderWrapper> _signatureHelpProviders;

        public WorkSession([NotNull] ILanguage language, [CanBeNull] IWorkSessionOptions options = null) {
            _language = Argument.NotNull(nameof(language), language);
            _options = options;

            SelfDebug = (options?.SelfDebugEnabled ?? false) ? new SelfDebug() : null;
        }

        public void ChangeLanguage([NotNull] ILanguage language) {
            Argument.NotNull(nameof(language), language);
            if (language == _language)
                return;
            _language = language;
            Reset();
        }

        public void ChangeParseOptions([NotNull] string key, [NotNull] Func<ParseOptions, ParseOptions> change) {
            Argument.NotNull(nameof(key), key);
            Argument.NotNull(nameof(change), change);

            if (_parseOptionsChanges.TryGetValue(key, out var current) && current == change)
                return;
            _parseOptionsChanges[key] = change;
            if (_workspace != null && change(Project.ParseOptions) == Project.ParseOptions)
                return;
            Reset();
        }

        public void ChangeCompilationOptions([NotNull] string key, [NotNull] Func<CompilationOptions, CompilationOptions> change) {
            Argument.NotNull(nameof(key), key);
            Argument.NotNull(nameof(change), change);
            if (_compilationOptionsChanges.TryGetValue(key, out var current) && current == change)
                return;
            _compilationOptionsChanges[key] = change;
            if (_workspace != null && change(Project.CompilationOptions) == Project.CompilationOptions)
                return;
            Reset();
        }

        private void Reset() {
            _workspace?.Dispose();
            _workspace = null;
        }

        private void Initialize() {
            var projectId = ProjectId.CreateNewId();

            var parseOptions = _options?.GetDefaultParseOptionsByLanguageName?.Invoke(Language.Name) ?? Language.DefaultParseOptions;
            foreach (var change in _parseOptionsChanges.Values) {
                parseOptions = change(parseOptions);
            }
            var compilationOptions = _options?.GetDefaultCompilationOptionsByLanguageName?.Invoke(Language.Name) ?? Language.DefaultCompilationOptions;
            foreach (var change in _compilationOptionsChanges.Values) {
                compilationOptions = change(compilationOptions);
            }
            var metadataReferences = _options?.GetDefaultMetadataReferencesByLanguageName?.Invoke(Language.Name) ?? Language.DefaultAssemblyReferences;

            var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), "_", "_", Language.Name,
                parseOptions: parseOptions,
                compilationOptions: compilationOptions,
                metadataReferences: metadataReferences,
                analyzerReferences: Language.DefaultAnalyzerReferences
            );
            var documentId = DocumentId.CreateNewId(projectId);
            _sourceText = _sourceText ?? SourceText.From("");

            _workspace = new CustomWorkspace(Language.HostServices);
            var solution = _workspace.CurrentSolution
                .AddProject(projectInfo)
                .AddDocument(documentId, "_", _sourceText);
            solution = _workspace.SetCurrentSolution(solution);
            _workspace.OpenDocument(documentId);
            _document = solution.GetDocument(documentId);

            InitializeCompletion();

            _analyzers = Language.DefaultAnalyzers;
            _codeFixProviders = Language.DefaultCodeFixProvidersIndexedByDiagnosticIds;
            _signatureHelpProviders = Language.DefaultSignatureHelpProviders;
        }

        private void InitializeCompletion() {
            var completionService = CompletionService.GetService(_document);
            if (completionService == null)
                throw new Exception("Failed to retrieve the completion service.");
            _completion = new Completion(completionService);
        }

        public void RevertTo(SourceText sourceText, Solution solution) {
            if (_workspace != solution.Workspace)
                throw new InvalidOperationException("Solution revert cannot be performed if session options have changed.");
            // ReSharper disable once PossibleNullReferenceException
            _document = _workspace.SetCurrentSolution(solution).GetDocument(_document.Id);
            _sourceText = sourceText;
            InitializeCompletion();
        }

        public ILanguage Language => _language;
        public IWorkSessionOptions Options => _options;
        public int CursorPosition { get; set; }

        public SourceText SourceText {
            get {
                EnsureInitialized();
                return _sourceText;
            }
            set {
                EnsureInitialized();
                if (value == _sourceText)
                    return;
                _sourceText = value;
                _documentOutOfDate = true;
            }
        }

        public Document Document {
            get {
                EnsureDocumentUpToDate();
                return _document;
            }
        }

        [NotNull]
        public Completion Completion {
            get {
                EnsureInitialized();
                return _completion;
            }
        }
        
        [NotNull] public IList<CodeAction> CurrentCodeActions { get; } = new List<CodeAction>();
        [CanBeNull] public CurrentSignatureHelp? CurrentSignatureHelp { get; set; }

        public CustomWorkspace Workspace {
            get {
                EnsureDocumentUpToDate();
                return _workspace;
            }
        }
        public Project Project => Document.Project;

        public ImmutableArray<DiagnosticAnalyzer> Analyzers {
            get {
                EnsureInitialized();
                return _analyzers;
            }
        }

        public ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> CodeFixProviders {
            get {
                EnsureInitialized();
                return _codeFixProviders;
            }
        }

        public ImmutableArray<ISignatureHelpProviderWrapper> SignatureHelpProviders {
            get {
                EnsureInitialized();
                return _signatureHelpProviders;
            }
            private set { _signatureHelpProviders = value; }
        }

        public IDictionary<string, string> RawOptionsFromClient { get; } = new Dictionary<string, string>();
        [CanBeNull] public SelfDebug SelfDebug { get; }
        public IDictionary<string, object> ExtensionData { get; } = new Dictionary<string, object>();

        private void EnsureInitialized() {
            if (_workspace != null)
                return;
            Initialize();
        }

        private void EnsureDocumentUpToDate() {
            EnsureInitialized();
            if (!_documentOutOfDate)
                return;

            var document = _document.WithText(_sourceText);
            // ReSharper disable once PossibleNullReferenceException
            if (!_workspace.TryApplyChanges(document.Project.Solution))
                throw new Exception("Failed to apply changes to workspace.");
            _document = _workspace.CurrentSolution.GetDocument(document.Id);
            _documentOutOfDate = false;
        }

        public async Task<IReadOnlyList<TextChange>> RollbackWorkspaceChangesAsync() {
            EnsureDocumentUpToDate();
            var oldProject = _document.Project;
            // ReSharper disable once PossibleNullReferenceException
            var newProject = _workspace.CurrentSolution.GetProject(Project.Id);
            if (newProject == oldProject)
                return NoTextChanges;

            var newText = await newProject.GetDocument(_document.Id).GetTextAsync().ConfigureAwait(false);
            _document = _workspace.SetCurrentSolution(oldProject.Solution).GetDocument(_document.Id);

            return newText.GetTextChanges(_sourceText);
        }

        public void Dispose() {
            _workspace?.Dispose();
        }
    }
}