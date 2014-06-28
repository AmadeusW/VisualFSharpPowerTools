﻿namespace FSharpVSPowerTools.ProjectSystem

open System
open System.IO
open System.ComponentModel.Composition
open Microsoft.VisualStudio
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Editor
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.VisualStudio.TextManager.Interop

open FSharpVSPowerTools

open FSharp.ViewModule.Progress

type FilePath = string

[<RequireQualifiedAccess; NoComparison>]
type SymbolDeclarationLocation = 
    | File
    | Projects of IProjectProvider list // Source file where a symbol is declared may be included into several projects

and IProjectProvider =
    abstract IsForStandaloneScript: bool
    abstract ProjectFileName: string
    abstract TargetFramework: FSharpTargetFramework
    abstract CompilerOptions: string []
    abstract SourceFiles: string []
    abstract FullOutputFilePath: string
    abstract GetReferencedProjects: unit -> IProjectProvider list
    abstract GetAllReferencedProjectFileNames: unit -> string list
    abstract GetProjectCheckerOptions: LanguageService -> Async<ProjectOptions>

[<Export>]
type VSLanguageService
    [<ImportingConstructor>] 
    (editorFactory: IVsEditorAdaptersFactoryService, 
     fsharpLanguageService: FSharpLanguageService,
     openDocumentsTracker: OpenDocumentsTracker) =

    let instance = LanguageService (ignore, FileSystem openDocumentsTracker)
    
    let getProjectOptions (project: IProjectProvider) =
        async {
            let! opts = project.GetProjectCheckerOptions(instance)
            let projectFiles = Set.ofArray project.SourceFiles 
            let openDocumentsChangeTimes = 
                    openDocumentsTracker.MapOpenDocuments (fun (KeyValue (file, doc)) -> file, doc)
                    |> Seq.choose (fun (file, doc) -> 
                        if doc.Document.IsDirty && projectFiles |> Set.contains file then Some doc.LastChangeTime else None)
                    |> Seq.toList
        
            return 
                match openDocumentsChangeTimes with
                | [] -> opts
                | changeTimes -> { opts with LoadTime = List.max (opts.LoadTime::changeTimes) }
        }

    let buildQueryLexState (textBuffer: ITextBuffer) source defines line =
        try
            let vsColorState = editorFactory.GetBufferAdapter(textBuffer) :?> IVsTextColorState
            let colorState = fsharpLanguageService.GetColorStateAtStartOfLine(vsColorState, line)
            fsharpLanguageService.LexStateOfColorState(colorState)
        with e ->
            debug "[Language Service] %O exception occurs while querying lexing states." e
            Lexer.queryLexState source defines line

    let filterSymbolUsesDuplicates (uses: FSharpSymbolUse[]) =
        uses
        |> Seq.map (fun symbolUse -> (symbolUse.FileName, symbolUse))
        |> Seq.groupBy (fst >> Path.GetFullPathSafe)
        |> Seq.map (fun (_, symbolUses) -> 
            symbolUses 
            |> Seq.map snd 
            |> Seq.distinctBy (fun s -> s.RangeAlternate))
        |> Seq.concat
        |> Seq.toArray
        
    member x.GetSymbol(point: SnapshotPoint, projectProvider: IProjectProvider) =
        let source = point.Snapshot.GetText()
        let line = point.Snapshot.GetLineNumberFromPosition point.Position
        let col = point.Position - point.GetContainingLine().Start.Position
        let lineStr = point.GetContainingLine().GetText()                
        let args = projectProvider.CompilerOptions
        let snapshotSpanFromRange (snapshot: ITextSnapshot) (lineStart, colStart, lineEnd, colEnd) =
            let startPos = snapshot.GetLineFromLineNumber(lineStart).Start.Position + colStart
            let endPos = snapshot.GetLineFromLineNumber(lineEnd).Start.Position + colEnd
            SnapshotSpan(snapshot, startPos, endPos - startPos)
                                
        Lexer.getSymbol source line col lineStr args (buildQueryLexState point.Snapshot.TextBuffer)
        |> Option.map (fun symbol -> snapshotSpanFromRange point.Snapshot symbol.Range, symbol)

    member x.TokenizeLine(textBuffer: ITextBuffer, args: string[], line) =
        let snapshot = textBuffer.CurrentSnapshot
        let source = snapshot.GetText()
        let lineStr = snapshot.GetLineFromLineNumber(line).GetText()
        Lexer.tokenizeLine source args line lineStr (buildQueryLexState textBuffer)

    member x.ParseFileInProject (currentFile: string, source, projectProvider: IProjectProvider) =
        async {
            let! opts = projectProvider.GetProjectCheckerOptions instance
            return! instance.ParseFileInProject(opts, currentFile, source) 
        }

    member x.ProcessNavigableItemsInProject(openDocuments, projectProvider: IProjectProvider, processNavigableItems, ct) =
        instance.ProcessParseTrees(
            projectProvider.ProjectFileName, 
            openDocuments, 
            projectProvider.SourceFiles, 
            projectProvider.CompilerOptions, 
            projectProvider.TargetFramework, 
            (Navigation.NavigableItemsCollector.collect >> processNavigableItems), 
            ct)        

    member x.FindUsages (word: SnapshotSpan, currentFile: string, currentProject: IProjectProvider, projectsToCheck: IProjectProvider list, ?progress : OperationState -> unit) =
        async {
            try                 
                let (_, _, endLine, endCol) = word.ToRange()
                let source = word.Snapshot.GetText()
                let currentLine = word.Start.GetContainingLine().GetText()
                let framework = currentProject.TargetFramework
                let args = currentProject.CompilerOptions
            
                debug "[Language Service] Get symbol references for '%s' at line %d col %d on %A framework and '%s' arguments" 
                      (word.GetText()) endLine endCol framework (String.concat " " args)
            
                reportProgress progress (Reporting(Resource.findSymbolUseCurrentProject))
                let! currentProjectOptions = getProjectOptions currentProject
                reportProgress progress (Reporting(Resource.findSymbolUseOtherProjects))
                let! projectsToCheckOptions = 
                    projectsToCheck 
                    |> List.toArray
                    |> Async.Array.map getProjectOptions

                reportProgress progress (Reporting(Resource.findSymbolUseAllProjects))
                let! res =
                    instance.GetUsesOfSymbolInProjectAtLocationInFile
                        (currentProjectOptions, projectsToCheckOptions, currentFile, source, endLine, endCol, 
                         currentLine, args, buildQueryLexState word.Snapshot.TextBuffer, progress)
                return 
                    res 
                    |> Option.map (fun (symbol, lastIdent, refs) -> 
                        symbol, lastIdent, filterSymbolUsesDuplicates refs)
            with e ->
                debug "[Language Service] %O exception occurs while updating." e
                return None }

    member x.FindUsagesInFile (word: SnapshotSpan, sym: Symbol, currentFile: string, projectProvider: IProjectProvider, stale) =
        async {
            try 
                let (_, _, endLine, endCol) = word.ToRange()
                let framework = projectProvider.TargetFramework
                let args = projectProvider.CompilerOptions
            
                debug "[Language Service] Get symbol references for '%s' at line %d col %d on %A framework and '%s' arguments" 
                      (word.GetText()) endLine endCol framework (String.concat " " args)
            
                let! res = x.GetFSharpSymbolUse (word, sym, currentFile, projectProvider, stale)
                return 
                    res 
                    |> Option.map (fun (_, checkResults) -> 
                        x.FindUsagesInFile (word, sym, checkResults)
                        |> Async.map (Option.map (fun (symbol, ident, refs) -> symbol, ident, filterSymbolUsesDuplicates refs)))
            with e ->
                debug "[Language Service] %O exception occurs while updating." e
                return None }

    member x.FindUsagesInFile (word: SnapshotSpan, sym: Symbol, fileScopedCheckResults: ParseAndCheckResults) =
        async {
            try 
                let (_, _, endLine, _) = word.ToRange()
                let currentLine = word.Start.GetContainingLine().GetText()
            
                debug "[Language Service] Get symbol references for '%s' at line %d col %d" (word.GetText()) endLine sym.RightColumn
                let! res = fileScopedCheckResults.GetUsesOfSymbolInFileAtLocation (endLine, sym.RightColumn, currentLine, sym.Text)
                return res |> Option.map (fun (symbol, ident, refs) -> symbol, ident, filterSymbolUsesDuplicates refs)
            with e ->
                debug "[Language Service] %O exception occurs while finding usages in file." e
                return None
        }

    member x.GetFSharpSymbolUse (word: SnapshotSpan, symbol: Symbol, currentFile: string, projectProvider: IProjectProvider, stale) = 
        async {
            let (_, _, endLine, _) = word.ToRange()
            let source = word.Snapshot.GetText()
            let currentLine = word.Start.GetContainingLine().GetText()
            let! opts = projectProvider.GetProjectCheckerOptions instance
            let! results = instance.ParseAndCheckFileInProject(opts, currentFile, source, stale)
            let! symbol = results.GetSymbolUseAtLocation (endLine+1, symbol.RightColumn, currentLine, [symbol.Text])
            return symbol |> Option.map (fun s -> s, results)
        }

    member x.GetAllUsesOfAllSymbolsInFile (snapshot: ITextSnapshot, currentFile: string, projectProvider: IProjectProvider, stale) = 
        async {
            let source = snapshot.GetText()
            let args = projectProvider.CompilerOptions
            let lexer = 
                let getLineStr line =
                    let lineStart,_,_,_ = SnapshotSpan(snapshot, 0, snapshot.Length).ToRange()
                    let lineNumber = line - lineStart
                    snapshot.GetLineFromLineNumber(lineNumber).GetText() 

                { new ILexer with
                    member x.GetSymbolFromTokensAtLocation (tokens, line, col) =
                        Lexer.getSymbolFromTokens tokens line col (getLineStr line)
                    member x.TokenizeLine line =
                        Lexer.tokenizeLine source args line (getLineStr line) (buildQueryLexState snapshot.TextBuffer) }

            let! opts = projectProvider.GetProjectCheckerOptions instance
            let! symbolUses = instance.GetAllUsesOfAllSymbolsInFile(opts, currentFile, source, stale)
            return symbolUses, lexer
        }

    /// Get all the uses in the project of a symbol in the given file (using 'source' as the source for the file)
    member x.IsSymbolUsedInProjects(symbol: FSharpSymbol, currentProjectName: FilePath, projects: IProjectProvider list) =
        async {
            let! projectOptions = 
                projects 
                |> List.toArray
                |> Async.Array.map getProjectOptions
            return! instance.IsSymbolUsedInProjects (symbol, currentProjectName, projectOptions) }

    member x.InvalidateProject (projectProvider: IProjectProvider) = 
        async {
            let! opts = projectProvider.GetProjectCheckerOptions(instance) 
            return instance.Checker.InvalidateConfiguration opts
        }

    member x.ClearCaches() = 
        debug "[Language Service] Clearing FCS caches."
        instance.Checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()
    
    member x.Checker = instance.Checker
