﻿module internal FSharp.DynamoDB.RecordSchema

open System
open System.Collections.Generic

open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model

type KeyStructure =
    | HashKeyOnly of hashKeyProperty:RecordPropertyInfo
    | Combined of hashKeyProperty:RecordPropertyInfo * rangeKeyProperty:RecordPropertyInfo
    | DefaultHashKey of hkName:string * hkValue:obj * hkPickler:Pickler * rangeKeyProperty:RecordPropertyInfo
with
    static member ExtractKey(keyStructure : KeyStructure, key : TableKey) =
        let dict = new Dictionary<string, AttributeValue> ()
        let extractKey name (pickler : Pickler) (value:obj) =
            if obj.ReferenceEquals(value, null) then invalidArg name "Key value was not specified."
            let av = pickler.PickleUntyped value |> Option.get
            dict.Add(name, av)

        match keyStructure with
        | HashKeyOnly hkp -> extractKey hkp.Name hkp.Pickler key.HashKey
        | Combined(hkp,rkp) ->
            extractKey hkp.Name hkp.Pickler key.HashKey
            extractKey rkp.Name rkp.Pickler key.RangeKey
        | DefaultHashKey(name, value, pickler, rkp) ->
            if key.IsHashKeySpecified then
                extractKey name pickler key.HashKey
            else
                let av = value |> pickler.PickleUntyped |> Option.get
                dict.Add(name, av)

            extractKey rkp.Name rkp.Pickler key.RangeKey

        dict


    static member ExtractKey(keyStructure : KeyStructure, recordInfo : RecordInfo, record : 'Record) =
        let inline getValue (rp : RecordPropertyInfo) = rp.PropertyInfo.GetValue(record)
        match keyStructure with
        | HashKeyOnly hkp -> let hashKey = getValue hkp in TableKey.Hash hashKey
        | DefaultHashKey(_, hashKey, _, rkp) ->
            let rangeKey = getValue rkp
            TableKey.Combined(hashKey, rangeKey)
        | Combined(hkp,rkp) ->
            let hashKey = getValue hkp
            let rangeKey = getValue rkp
            TableKey.Combined(hashKey, rangeKey)

    static member FromRecordInfo (recordInfo : RecordInfo) =
        let hkcaOpt = recordInfo.Type.TryGetAttribute<HashKeyConstantAttribute> ()

        match recordInfo.Properties |> Array.filter (fun p -> p.IsHashKey) with
        | [|hashKeyP|] when Option.isSome hkcaOpt ->
            invalidArg (string recordInfo.Type) "Cannot attach HashKey attribute to records containing RangeKeyConstant attribute."

        | [|hashKeyP|] ->
            match recordInfo.Properties |> Array.filter (fun p -> p.IsRangeKey) with
            | [||] -> HashKeyOnly hashKeyP
            | [|rangeKeyP|] -> Combined(hashKeyP, rangeKeyP)
            | _ -> invalidArg (string recordInfo.Type) "Found more than one record fields carrying the RangeKey attribute."

        | [||] when Option.isSome hkcaOpt ->
            match recordInfo.Properties |> Array.filter (fun p -> p.IsRangeKey) with
            | [||] -> invalidArg (string recordInfo.Type) "Records carrying the RangeKeyConstant attribute must specify a RangeKey property."
            | [|rangeKeyP|] -> 
                let hkca = Option.get hkcaOpt
                if not <| isValidFieldName hkca.Name then
                    invalidArg hkca.Name "invalid hashkey name; must be alphanumeric and should not begin with a number."

                if recordInfo.Properties |> Array.exists(fun p -> p.Name = hkca.Name) then
                    invalidArg (string recordInfo.Type) "Default HashKey attribute contains conflicting name."

                let pickler = Pickler.resolveUntyped hkca.HashKeyType
                DefaultHashKey(hkca.Name, hkca.HashKey, pickler, rangeKeyP)

            | _ -> invalidArg (string recordInfo.Type) "Found more than one record fields carrying the RangeKey attribute."

        | [||] -> invalidArg (string recordInfo.Type) "Found no record fields carrying the HashKey attribute."
        | _ -> invalidArg (string recordInfo.Type) "Found more than one record fields carrying the HashKey attribute."



type TableKeySchema with
    static member OfKeyStructure(ks : KeyStructure) =
        let mkTableKeySchema h r = { HashKey = h ; RangeKey = r }
        let mkKeySchema (name : string) (pickler : Pickler) =
            if pickler.PicklerType <> PicklerType.Value then
                invalidArg name <| "DynamoDB Key attributes do not support serialization attributes."

            let keyType =
                match pickler.PickleType with
                | PickleType.String -> ScalarAttributeType.S
                | PickleType.Number -> ScalarAttributeType.N
                | PickleType.Bytes -> ScalarAttributeType.B
                | _ -> invalidArg name <| sprintf "Unsupported type '%O' for DynamoDB Key attribute." pickler.Type

            { AttributeName = name ; KeyType = keyType }

        match ks with
        | HashKeyOnly rp -> mkTableKeySchema (mkKeySchema rp.Name rp.Pickler) None
        | Combined(hk, rk) -> mkTableKeySchema (mkKeySchema hk.Name hk.Pickler) (Some (mkKeySchema rk.Name rk.Pickler))
        | DefaultHashKey(name,_,pickler,rk) -> mkTableKeySchema (mkKeySchema name pickler) (Some (mkKeySchema rk.Name rk.Pickler))

    static member OfTableDescription (td : TableDescription) =
        let mkKeySchema (kse : KeySchemaElement) =
            let ad = td.AttributeDefinitions |> Seq.find (fun ad -> ad.AttributeName = kse.AttributeName)
            { AttributeName = kse.AttributeName ; KeyType = ad.AttributeType }
            
        let hk = td.KeySchema |> Seq.find (fun ks -> ks.KeyType = KeyType.HASH) |> mkKeySchema
        let rk = td.KeySchema |> Seq.tryPick (fun ks -> if ks.KeyType = KeyType.RANGE then Some(mkKeySchema ks) else None)
        { HashKey = hk ; RangeKey = rk }

    member schema.CreateCreateTableRequest (tableName : string, provisionedThroughput : ProvisionedThroughput) =
        let ctr = new CreateTableRequest(TableName = tableName)
        let addKey kt (ks : KeyAttributeSchema) =
            ctr.KeySchema.Add <| new KeySchemaElement(ks.AttributeName, kt)
            ctr.AttributeDefinitions.Add <| new AttributeDefinition(ks.AttributeName, ks.KeyType)

        addKey KeyType.HASH schema.HashKey
        schema.RangeKey |> Option.iter (fun rk -> addKey KeyType.RANGE rk)
        ctr.ProvisionedThroughput <- provisionedThroughput
        ctr