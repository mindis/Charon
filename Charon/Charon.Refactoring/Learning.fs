﻿namespace Charon.Refactoring

module Learning =

    open Charon.Refactoring
    open Charon.Refactoring.Entropy
    open Charon.Refactoring.Featurization
    open Charon.Refactoring.Tree

    type Variable =
        | Disc of int [][] // outcome, and corresponding observation indexes
        | Cont of (float option*int) [] // values, and label value

    type Dataset = { Classes:int; Outcomes:int []; Features: Variable [] }
    
    type Settings = { MinLeaf:int; Holdout:float }

    let discConv (x:Value) =
        match x with
        | Int(x) -> x
        | _      -> failwith "Not an int - Boom!"

    let contConv (x:Value) =
        match x with
        | Float(x) -> x
        | _        -> failwith "Not a float - Boom!"

    let continuous (data:('l*'a) seq) (feature:'a -> Value) (label:'l -> Value) =
        data
        |> Seq.map (fun (lbl,obs) -> feature obs |> contConv, label lbl |> discConv |> Option.get)
        |> Seq.toArray

    let discrete (data:'a seq) (feature:'a -> Value) =
        data
        |> Seq.mapi (fun i obs -> i, feature obs |> discConv)
        |> Seq.filter (fun (i,v) -> Option.isSome v)
        |> Seq.map (fun (i,v) -> i, Option.get v)
        |> Seq.groupBy (fun (i,v) -> v)
        |> Seq.sortBy fst
        |> Seq.map (fun (v,is) -> is |> Seq.map fst |> Seq.toArray)
        |> Seq.toArray

    let countClasses (data:'a seq) (feature:'a -> Value) =
        data
        |> Seq.map (fun obs -> feature obs |> discConv |> Option.get)
        |> Seq.distinct
        |> Seq.length

    let intlabels (data: 'a seq) (feature:'a -> Value) =
        data 
        |> Seq.map (fun obs -> feature obs |> discConv |> Option.get) 
        |> Seq.toArray
        
    let translators (data:('l*'a) seq) (labels:(string*Feature<'l>), (features:(string*Feature<'a>) list)) = 
        
        let ls = data |> Seq.map fst
        let obs = data |> Seq.map snd

        let labelsMap = createFeatureMap ls (snd labels)
        let labelizer = converter (snd labels) (labelsMap.OutsideIn)
        
        let featurizers = 
            features
            |> List.map (fun (n,f) -> 
                let map = createFeatureMap obs f
                converter f (map.OutsideIn))

        labelizer, featurizers
            
    let prepare (data:('l*'a) seq) ((labels:Converter<'l>), (features:Converter<'a> list)) =
        
        let valueType,lblconverter = labels

        // Currently filtering out every observation that has no label.
        // Might want to revisit later, in case missing labels can
        // provide information on classifier reliability?
        let haslabel (x:Value) =
            match x with
            | Int(v) -> v |> Option.isSome
            | Float(v) -> v |> Option.isSome

        let data = 
            data 
            |> Seq.filter (fun (lbl,obs) -> lblconverter lbl |> haslabel)

        let classes,labels = 
            match valueType with
            | Continuous -> failwith "Regression not implemented yet."
            | Discrete   -> 
                let ls = data |> Seq.map fst
                countClasses ls lblconverter, intlabels ls lblconverter

        let transformed = 
            let observations = data |> Seq.map snd
            features
            |> List.map (fun feat ->
                let valueType, converter = feat
                match valueType with
                | Discrete   -> discrete observations converter |> Disc
                | Continuous -> continuous data converter lblconverter |> Cont)
            |> List.toArray

        { Classes = classes; Outcomes = labels; Features = transformed }

    let applyFilter (filter:filter) (data: _ []) =
        filter |> Array.map (fun i -> data.[i])

    let countCases (data:_ []) =
        data |> Seq.countBy id |> Seq.map snd |> Seq.toArray

    let mostLikely (outcomes: _ []) = 
        outcomes 
        |> Seq.countBy id 
        |> Seq.maxBy snd 
        |> fst

    let arrayFilter (array:int[]) (filter:int[]) =
        filter |> Array.filter (fun x -> array |> Array.exists (fun z -> z = x))

    let filteredBy (filter:filter) (feature:Variable) =
        match feature with
        | Disc(indexes) -> 
            [| for i in indexes -> arrayFilter i filter |] |> Disc
        | Cont(x) -> 
            [| for i in filter -> x.[i] |] |> Cont

    let conditional (data:int[][]) (labels:_[]) =
        let total = data |> Array.sumBy (fun x -> Array.length x |> float)
        data 
        |> Seq.map (fun x -> seq { for i in x -> labels.[i] } |> countFrom)
        |> Seq.map (fun x -> Array.sum x |> float, h x)
        |> Seq.sumBy (fun (count,h) -> h * count / total)
        
    let bestSplit (feature:Variable) (labels:int[]) (classes:int) =
        match feature with
        | Disc(x) -> conditional x labels, []
        | Cont(x) -> Continuous.analyze classes x 
        
    let selectFeature (dataset: Dataset) // full dataset
                      (filter: filter) // indexes of observations in use
                      (remaining: int Set) = // indexes of usable features 
        
        let labels = dataset.Outcomes |> applyFilter filter
        let initialEntropy = labels |> countCases |> h

        let candidates = 
            seq { for i in remaining -> i, dataset.Features.[i] |> filteredBy filter }
            |> Seq.map (fun (i,f) -> i,f, bestSplit f (dataset.Outcomes) (dataset.Classes))
            |> Seq.map (fun (i,f,(h,s)) -> (i,f,s,initialEntropy - h)) // replace h with gain
            |> Seq.filter (fun (_,_,_,g) -> g > 0.) 

        if (Seq.isEmpty candidates) then None
        else candidates |> Seq.maxBy (fun (_,_,_,g) -> g) |> fun (i,f,s,_) -> (i,f,s) |> Some

    let rec train (dataset:Dataset) (filter:filter) (remaining:int Set) (settings:Settings) =

        let mostLikely () = dataset.Outcomes |> applyFilter filter |> mostLikely
        
        if (remaining = Set.empty) then
            Leaf(mostLikely ())
        elif (Array.length filter <= settings.MinLeaf) then
            Leaf(mostLikely ())
        else
            let candidates = remaining
            let best = selectFeature dataset filter candidates
            
            match best with
            | None -> Leaf(mostLikely ())
            | Some(i,f,s) -> // index, feature, splits, gain
                let remaining = remaining |> Set.remove i
                match f with
                | Disc(indexes) ->
                    let branch = { FeatIndex = i; Default = mostLikely () }
                    Branch(Cat(branch, [| 
                                          for filt in indexes -> 
                                              if Array.length filt = 0 then Leaf(mostLikely ())
                                              else train dataset filt remaining settings 
                                       |]))
                | Cont(_) ->
                    let branch = { NumBranch.FeatIndex = i; Default = mostLikely (); Splits = s }
                    let feat = // this is ugly as hell
                        match dataset.Features.[i] with
                        | Cont(x) -> x
                        | _ -> failwith "kaboom"
                    let filters = Continuous.subindex feat filter s
                    Branch(Num(branch, [|
                                           for kv in filters ->
                                              let filt = kv.Value
                                              if Array.length filt = 0 then Leaf(mostLikely ())
                                              else train dataset filt remaining settings 
                                       |]))

    type Results<'l,'a> = 
        {   Classifier:'a -> string;
            Tree: Tree;
            Settings: Settings;
            TrainingQuality: float option;
            HoldoutQuality: float option;
            Pretty: string }

    let basicTree<'l,'a> (data:('l*'a) seq) ((labels:string*Feature<'l>), (features:(string*Feature<'a>) list)) =

        let fs = List.length features

        let labelsMap = createFeatureMap (data |> Seq.map fst) (snd labels)
        let predictionToLabel = labelsMap.InsideOut
        let maps = createTranslators data labels features

        let (labelizer,featurizers) = translators data (labels,features)
        
        // TODO: inject user-defined settings
        let settings =  { MinLeaf = 5; Holdout = 0.20 }
                
        let dataset = prepare data (labelizer,featurizers)

        // TODO: improve with injected rng, proper sampling
        let rng = System.Random()
        let xs = Array.length dataset.Outcomes
        let trainingsample,validationsample = [| 0 .. (xs - 1) |] |> Array.partition (fun x -> rng.NextDouble() > settings.Holdout)

        let tree = train dataset trainingsample ([0..(fs-1)] |> Set.ofList) settings

        let converter = 
            let fs = featurizers |> List.unzip |> snd
            fun (obs:'a) -> List.map (fun f -> f obs) fs |> List.toArray
            
        let classifier = fun (obs:'a) -> labelsMap.InsideOut.[ decide tree (converter obs) ]

        let predictions =
            (dataset.Outcomes, data) 
            ||> Seq.zip
            |> Seq.map (fun (l,(_,v)) -> if l = decide tree (converter v) then 1. else 0.)
            |> Seq.toArray

        let trainingquality = 
            if (Array.length trainingsample = 0) then None
            else
                seq { for i in trainingsample -> predictions.[i] }
                |> Seq.average |> Some
        let holdoutquality = 
            if (Array.length validationsample = 0) then None
            else seq { for i in validationsample -> predictions.[i] } |> Seq.average |> Some

        let view = pretty tree maps

        { Classifier = classifier;
          Tree = tree;
          Settings = settings;
          TrainingQuality = trainingquality;
          HoldoutQuality = holdoutquality
          Pretty = view;
        }
