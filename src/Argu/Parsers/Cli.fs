﻿[<AutoOpen>]
module internal Argu.CliParser

open System
open System.Text.RegularExpressions

type CliParseToken =
    | EndOfStream
    | CliParam of token:string * switch:string * caseInfo:UnionCaseArgInfo * assignment:Assignment
    | UnrecognizedOrArgument of token:string
    | GroupedParams of token:string * switches:string[] 
    | HelpArgument of token:string

type CliTokenReader(inputs : string[]) =
    let mutable position = 0
    let mutable segmentStartPos = 0
    let mutable isPeekedValue = false
    let mutable peekedValue = Unchecked.defaultof<CliParseToken>

    static let assignmentRegex = 
        let escapedChars = new String(validSeparatorChars) |> Regex.Escape
        new Regex(sprintf "^([%s]+)(.*)$" escapedChars, RegexOptions.Compiled)

    member __.BeginCliSegment() =
        segmentStartPos <- position

    /// returns the substring that corresponds to the current argument being parsed
    /// e.g "-port 2" from "-Cf -port 2 -bar"
    member __.CurrentSegment =
        inputs.[segmentStartPos .. position - 1] |> flattenCliTokens

    member __.GetNextToken (peekOnly : bool) (argInfo : UnionArgInfo) =
        // continuation which decides how to consume parsed token
        let inline kont result =
            if peekOnly then 
                peekedValue <- result 
                isPeekedValue <- true
            else 
                position <- position + 1 
                isPeekedValue <- false
                peekedValue <- Unchecked.defaultof<_>

            result

        let inline tryExtractGroupedSwitches token =
            match argInfo.GroupedSwitchExtractor.Value token with
            | [||] -> UnrecognizedOrArgument token
            | args -> GroupedParams(token, args)

        if isPeekedValue then kont peekedValue
        elif position = inputs.Length then kont EndOfStream else

        let token = inputs.[position]
        
        match token with
        | token when argInfo.HelpParam.IsHelpFlag token -> HelpArgument token |> kont
        | token ->
            let mutable prefix = null
            let mutable case = Unchecked.defaultof<_>
            if argInfo.CliParamIndex.Value.TryGetPrefix(token, &prefix, &case) then
                if token = prefix then CliParam(token, prefix, case, NoAssignment) |> kont
                else
                    let m = assignmentRegex.Match token.[prefix.Length ..]
                    if not m.Success then tryExtractGroupedSwitches token |> kont
                    else
                        let sep = m.Groups.[1].Value
                        let value = m.Groups.[2].Value
                        let assignment = Assignment(prefix, sep, value)
                        CliParam(token, prefix, case, assignment) |> kont
            else
                tryExtractGroupedSwitches token |> kont

    member __.MoveNext() =
        if position < inputs.Length then 
            position <- position + 1 
            isPeekedValue <- false
            peekedValue <- Unchecked.defaultof<_>

    member __.IsCompleted = position = inputs.Length

type CliParseResultAggregator internal (argInfo : UnionArgInfo, stack : CliParseResultAggregatorStack) =
    let mutable resultCount = 0
    let mutable isSubCommandDefined = false
    let unrecognized = new ResizeArray<string>()
    let unrecognizedParseResults = new ResizeArray<obj>()
    let results = argInfo.Cases |> Array.map (fun _ -> new ResizeArray<UnionCaseParseResult> ())

    member val IsUsageRequested = false with get,set
    member __.ResultCount = resultCount
    member __.IsSubCommandDefined = isSubCommandDefined

    member __.AppendResult(result : UnionCaseParseResult) =
        if result.CaseInfo.Depth = argInfo.Depth then
            resultCount <- resultCount + 1
            let agg = results.[result.Tag]
            if result.CaseInfo.IsUnique && agg.Count > 0 then
                error argInfo ErrorCode.CommandLine "argument '%s' has been specified more than once." result.CaseInfo.Name

            if result.CaseInfo.Type = ArgumentType.SubCommand then
                isSubCommandDefined <- true

            agg.Add result
        else
            // this parse result corresponds to an inherited parameter
            // from a parent syntax. Use the ResultAggregator stack to
            // re-route the result to its matching aggregator
            if stack.TryDispatchResult result then ()
            else unrecognizedParseResults.Add result.Value

    member __.AppendUnrecognized(token:string) = unrecognized.Add token

    member __.ToUnionParseResults() = 
        { Cases = results |> Array.map (fun c -> c.ToArray()) ; 
          UnrecognizedCliParams = Seq.toList unrecognized ;
          UnrecognizedCliParseResults = Seq.toList unrecognizedParseResults ;
          IsUsageRequested = __.IsUsageRequested }

// this rudimentary stack implementation assumes that only one subcommand
// can occur within any particular context; no need implement popping etc.
// Note that inheritting subcommands is explicitly prohibited by the library.
and CliParseResultAggregatorStack (context : UnionArgInfo) =
    let offset = context.Depth
    let stack = new ResizeArray<CliParseResultAggregator>(capacity = 2)

    member self.TryDispatchResult(result : UnionCaseParseResult) =
        if result.CaseInfo.Depth < offset then false
        else
            stack.[result.CaseInfo.Depth - offset].AppendResult result
            true

    member self.CreateNextAggregator(argInfo : UnionArgInfo) =
        assert(stack.Count = argInfo.Depth - offset)
        let agg = new CliParseResultAggregator(argInfo, self)
        stack.Add agg
        agg


type CliParseState =
    {
        ProgramName : string
        Description : string option
        UsageStringCharWidth : int
        ResultStack : CliParseResultAggregatorStack
        Exiter : IExiter
        IgnoreUnrecognizedArgs : bool
        RaiseOnUsage : bool
        Reader : CliTokenReader
    }

/// parse the next command line argument and append to state
let rec private parseCommandLinePartial (state : CliParseState) (argInfo : UnionArgInfo) (aggregator : CliParseResultAggregator) =
    state.Reader.BeginCliSegment()

    match state.Reader.GetNextToken false argInfo with
    | EndOfStream -> ()
    | HelpArgument _ when state.RaiseOnUsage -> raise <| HelpText argInfo
    | HelpArgument _ -> aggregator.IsUsageRequested <- true
    | UnrecognizedOrArgument token when state.IgnoreUnrecognizedArgs -> aggregator.AppendUnrecognized token
    | UnrecognizedOrArgument token -> error argInfo ErrorCode.CommandLine "unrecognized argument: '%s'." token
    | GroupedParams(_, switches) ->
        for sw in switches do
            let caseInfo = argInfo.CliParamIndex.Value.[sw]
            match caseInfo.ParameterInfo with
            | Primitives [||] ->
                let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine sw [||]
                aggregator.AppendResult result
            | _ -> error argInfo ErrorCode.CommandLine "argument '%s' cannot be grouped with other switches." sw

    | CliParam(_, _, caseInfo, Assignment(name,sep,_)) when caseInfo.Arity <> 1 || not <| caseInfo.IsMatchingAssignmentSeparator sep ->
        error argInfo ErrorCode.CommandLine "invalid CLI syntax '%s%s<param>'." name sep

    | CliParam(_, name, caseInfo, _) when caseInfo.IsFirst && aggregator.ResultCount > 0 ->
        error argInfo ErrorCode.CommandLine "argument '%s' should precede all other arguments." name

    | CliParam(token, name, caseInfo, assignment) ->
        match caseInfo.ParameterInfo with
        | Primitives [|field|] when caseInfo.IsCustomAssignment ->
            match assignment with
            | NoAssignment -> error argInfo ErrorCode.CommandLine "argument '%s' missing an assignment." name
            | Assignment(_,_,eqp) ->
                let argument = 
                    try field.Parser eqp
                    with _ -> error argInfo ErrorCode.CommandLine "argument '%s' is assigned invalid value, should be <%s>." token field.Description

                let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine name [| argument |]
                aggregator.AppendResult result

        | Primitives [|kf;vf|] when caseInfo.IsCustomAssignment ->
            match state.Reader.GetNextToken true argInfo with
            | UnrecognizedOrArgument token ->
                match caseInfo.AssignmentParser.Value token with
                | Assignment(key,_,value) ->
                    let k = 
                        try kf.Parser key
                        with _ -> error argInfo ErrorCode.CommandLine "argument '%s' was given invalid key '%s', should be <%s>." state.Reader.CurrentSegment token kf.Description

                    let v = 
                        try vf.Parser value
                        with _ -> error argInfo ErrorCode.CommandLine "argument '%s' was given invalid value assignment '%s', should be <%s>." state.Reader.CurrentSegment token vf.Description

                    let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine name [|k;v|]
                    aggregator.AppendResult result
                    state.Reader.MoveNext()

                | NoAssignment ->
                    error argInfo ErrorCode.CommandLine "argument '%s' must be followed by assignment '%s%s%s'." 
                        caseInfo.Name kf.Description caseInfo.CustomAssignmentSeparator.Value vf.Description

            | CliParam(token,name,_,Assignment _) -> 
                error argInfo ErrorCode.CommandLine "argument '%s' was given invalid key name '%s' in '%s'." 
                    state.Reader.CurrentSegment name token
            | _ ->
                error argInfo ErrorCode.CommandLine "argument '%s' must be followed by assignment '%s%s%s'." 
                    caseInfo.Name kf.Description caseInfo.CustomAssignmentSeparator.Value vf.Description

        | Primitives fields ->
            let parseNextField (p : FieldParserInfo) =
                match state.Reader.GetNextToken true argInfo with
                | UnrecognizedOrArgument token ->
                    let result =
                        try p.Parser token
                        with _ -> error argInfo ErrorCode.CommandLine "parameter '%s' must be followed by <%s>, but was '%s'." state.Reader.CurrentSegment p.Description token

                    state.Reader.MoveNext()
                    result

                | CliParam(_, name, _, _)
                | HelpArgument name 
                | GroupedParams(name,_) -> error argInfo ErrorCode.CommandLine "parameter '%s' must be followed by <%s>, but was '%s'." state.Reader.CurrentSegment p.Description name
                | _ -> error argInfo ErrorCode.CommandLine "argument '%s' must be followed by <%s>." state.Reader.CurrentSegment p.Description
                        
            let parseSingleParameter () =
                let fields = fields |> Array.map parseNextField
                let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine name fields
                aggregator.AppendResult result

            if caseInfo.IsRest then
                while not state.Reader.IsCompleted do
                    parseSingleParameter()
            else
                parseSingleParameter()

        | OptionalParam(existential, field) when caseInfo.IsCustomAssignment ->
            let optArgument = existential.Accept { new IFunc<obj> with 
                member __.Invoke<'T> () =
                    match assignment with
                    | NoAssignment -> Option<'T>.None :> obj
                    | Assignment(_,_,eqp) -> 
                        let argument = 
                            try field.Parser eqp
                            with _ -> 
                                error argInfo ErrorCode.CommandLine "argument '%s' is assigned invalid value, should be <%s>." 
                                    token field.Description

                        argument :?> 'T |> Some :> obj }

            let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine name [| optArgument |]
            aggregator.AppendResult result

        | OptionalParam(existential, field) ->
            let optArgument = existential.Accept { new IFunc<obj> with
                member __.Invoke<'T> () =
                    match state.Reader.GetNextToken true argInfo with
                    | UnrecognizedOrArgument tok -> 
                        let argument = try Some(field.Parser tok :?> 'T) with _ -> None
                        match argument with Some _ -> state.Reader.MoveNext() | None -> ()
                        argument :> obj

                    | _ -> Option<'T>.None :> obj }

            let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine name [| optArgument |]
            aggregator.AppendResult result

        | ListParam(existential, field) ->
            let listArg = existential.Accept { new IFunc<obj> with
                member __.Invoke<'T>() =
                    let args = new ResizeArray<'T> ()
                    let rec gather () =
                        match state.Reader.GetNextToken true argInfo with
                        | UnrecognizedOrArgument token ->
                            let result = try Some (field.Parser token :?> 'T) with _ -> None
                            match result with
                            | None -> ()
                            | Some item ->
                                args.Add item
                                state.Reader.MoveNext()
                                gather()
                        | _ -> ()

                    do gather()
                    Seq.toList args :> obj
            }

            let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine name [| listArg |]
            aggregator.AppendResult result

        | SubCommand (existential, nestedUnion, _) ->
            let nestedResults = parseCommandLineInner state nestedUnion
            let result = 
                existential.Accept { new ITemplateFunc<obj> with
                    member __.Invoke<'Template when 'Template :> IArgParserTemplate> () =
                        new ParseResult<'Template>(nestedUnion, nestedResults, state.ProgramName, state.Description, state.UsageStringCharWidth, state.Exiter) :> obj }

            let result = mkUnionCase caseInfo aggregator.ResultCount ParseSource.CommandLine name [|result|]
            aggregator.AppendResult result

and private parseCommandLineInner (state : CliParseState) (argInfo : UnionArgInfo) =
    let results = state.ResultStack.CreateNextAggregator argInfo
    while not state.Reader.IsCompleted do parseCommandLinePartial state argInfo results
    if argInfo.IsRequiredSubcommand && not results.IsSubCommandDefined then
        error argInfo ErrorCode.CommandLine "no valid subcommand has been specified."
    results.ToUnionParseResults()

/// <summary>
///     Parse the entire command line
/// </summary>
and parseCommandLine (argInfo : UnionArgInfo) (programName : string) (description : string option) (width : int) (exiter : IExiter) 
                        (raiseOnUsage : bool) (ignoreUnrecognized : bool) (inputs : string []) =
    let state = {
        Reader = new CliTokenReader(inputs)
        ProgramName = programName
        ResultStack = new CliParseResultAggregatorStack(argInfo)
        Description = description
        UsageStringCharWidth = width
        RaiseOnUsage = raiseOnUsage
        IgnoreUnrecognizedArgs = ignoreUnrecognized
        Exiter = exiter
    }

    parseCommandLineInner state argInfo