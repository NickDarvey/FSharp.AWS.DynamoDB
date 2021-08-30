﻿namespace FSharp.AWS.DynamoDB

open System
open System.IO
open System.Runtime.Serialization.Formatters.Binary

open Amazon.DynamoDBv2

/// Declares that the carrying property contains the HashKey
/// for the record instance. Property type must be of type
/// string, number or byte array.
[<Sealed; AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type HashKeyAttribute() =
    inherit Attribute()

/// Declares that the carrying property contains the RangeKey
/// for the record instance. Property type must be of type
/// string, number or byte array.
[<Sealed; AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type RangeKeyAttribute() =
    inherit Attribute()

/// Declares that the carrying property should contain the RangeKey
/// for a global secondary index.
[<Sealed; AttributeUsage(AttributeTargets.Property)>]
type GlobalSecondaryHashKeyAttribute(indexName : string) =
    inherit Attribute()
    member __.IndexName = indexName

/// Declares that the carrying property should contain the HashKey
/// for a global secondary index.
[<Sealed; AttributeUsage(AttributeTargets.Property)>]
type GlobalSecondaryRangeKeyAttribute(indexName : string) =
    inherit Attribute()
    member __.IndexName = indexName

/// Declares the carrying property as local secondary index
/// in the table schema.
[<Sealed; AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type LocalSecondaryIndexAttribute private (indexName : string option) =
    inherit Attribute()
    new () = LocalSecondaryIndexAttribute(None)
    new (indexName : string) = LocalSecondaryIndexAttribute(Some indexName)
    member internal __.IndexName = indexName

/// Declares a constant HashKey attribute for the given record.
/// Records carrying this attribute should specify a RangeKey field.
[<Sealed; AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type ConstantHashKeyAttribute(name : string, hashkey : obj) =
    inherit Attribute()
    do
        if isNull name then raise <| ArgumentNullException("'Name' parameter cannot be null.")
        if isNull hashkey then raise <| ArgumentNullException("'HashKey' parameter cannot be null.")

    member __.Name = name
    member __.HashKey = hashkey
    member __.HashKeyType = hashkey.GetType()

/// Declares a constant RangeKey attribute for the given record.
/// Records carrying this attribute should specify a HashKey field.
[<Sealed; AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type ConstantRangeKeyAttribute(name : string, rangeKey : obj) =
    inherit Attribute()
    do
        if isNull name then raise <| ArgumentNullException("'Name' parameter cannot be null.")
        if isNull rangeKey then raise <| ArgumentNullException("'HashKey' parameter cannot be null.")

    member __.Name = name
    member __.RangeKey = rangeKey
    member __.HashKeyType = rangeKey.GetType()

/// Declares that annotated property should be represented
/// as string in the DynamoDB table. Only applies to
[<Sealed; AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type StringRepresentationAttribute() =
    inherit Attribute()

/// Specify a custom DynamoDB attribute name for the given record field.
[<AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type CustomNameAttribute(name : string) =
    inherit System.Attribute()
    do if isNull name then raise <| ArgumentNullException("'Name' parameter cannot be null.")
    member __.Name = name

/// Specifies that record deserialization should fail if not corresponding attribute
/// was fetched from the table.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Class)>]
type NoDefaultValueAttribute() =
    inherit System.Attribute()

/// Declares that the given property should be serialized using the given
/// Serialization/Deserialization methods before being uploaded to the table.
type internal IPropertySerializer =
    abstract PickleType : Type
    abstract Serialize   : value:'T -> obj
    abstract Deserialize : pickle:obj -> 'T

/// Declares that the given property should be serialized using the given
/// Serialization/Deserialization methods before being uploaded to the table.
[<AbstractClass; AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type PropertySerializerAttribute<'PickleType>() =
    inherit Attribute()
    /// Serializes a value to the given pickle type
    abstract Serialize   :  'T -> 'PickleType
    /// Deserializes a value from the given pickle type
    abstract Deserialize : 'PickleType -> 'T

    interface IPropertySerializer with
        member __.PickleType = typeof<'PickleType>
        member __.Serialize value = __.Serialize value :> obj
        member __.Deserialize pickle = __.Deserialize (pickle :?> 'PickleType)

/// Declares that the given property should be serialized using BinaryFormatter
[<AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type BinaryFormatterAttribute() =
    inherit PropertySerializerAttribute<byte[]>()

    override __.Serialize(value:'T) =
        let bfs = BinaryFormatter()
        use m = new MemoryStream()
        bfs.Serialize(m, value)
        m.ToArray()

    override __.Deserialize(pickle : byte[]) =
        let bfs = BinaryFormatter()
        use m = new MemoryStream(pickle)
        bfs.Deserialize(m) :?> 'T

/// Metadata on a table key attribute
type KeyAttributeSchema =
    {
        AttributeName : string
        KeyType : ScalarAttributeType
    }

/// Identifies type of DynamoDB table key schema
type KeySchemaType =
    | PrimaryKey
    | GlobalSecondaryIndex of indexName:string
    | LocalSecondaryIndex of indexName:string
with
    member kst.IndexName =
        match kst with
        | GlobalSecondaryIndex name
        | LocalSecondaryIndex name -> Some name
        | PrimaryKey -> None

/// DynamoDB table key schema description
type TableKeySchema =
    {
        HashKey : KeyAttributeSchema
        RangeKey : KeyAttributeSchema option
        Type : KeySchemaType
    }

/// Table entry key identifier
[<Struct; CustomEquality; NoComparison; StructuredFormatDisplay("{Format}")>]
type TableKey private (hashKey : obj, rangeKey : obj) =
    member __.HashKey = hashKey
    member __.RangeKey = rangeKey
    member __.IsRangeKeySpecified = notNull rangeKey
    member __.IsHashKeySpecified = notNull hashKey
    member private __.Format =
        match rangeKey with
        | null -> sprintf "{ HashKey = %A }" hashKey
        | rk ->
            match hashKey with
            | null -> sprintf "{ RangeKey = %A }" rk
            | hk -> sprintf "{ HashKey = %A ; RangeKey = %A }" hk rk

    override __.ToString() = __.Format

    override tk.Equals o =
        match o with
        | :? TableKey as tk' -> hashKey = tk'.HashKey && rangeKey = tk'.RangeKey
        | _ -> false

    override tk.GetHashCode() = hash2 hashKey rangeKey

    /// Defines a table key using provided HashKey
    static member Hash<'HashKey>(hashKey : 'HashKey) =
        if isNull hashKey then raise <| ArgumentNullException("HashKey must not be null")
        TableKey(hashKey, null)

    /// Defines a table key using provided RangeKey
    static member Range<'RangeKey>(rangeKey : 'RangeKey) =
        if isNull rangeKey then raise <| ArgumentNullException("RangeKey must not be null")
        TableKey(null, rangeKey)

    /// Defines a table key using combined HashKey and RangeKey
    static member Combined<'HashKey, 'RangeKey>(hashKey : 'HashKey, rangeKey : 'RangeKey) =
        if isNull hashKey then raise <| ArgumentNullException("HashKey must not be null")
        TableKey(hashKey, rangeKey)

/// Query (start/last evaluated) key identifier
[<Struct; CustomEquality; NoComparison; StructuredFormatDisplay("{Format}")>]
type QueryKey private (hashKey : obj, rangeKey : obj, primaryKey: TableKey option) =
    member __.HashKey = hashKey
    member __.RangeKey = rangeKey
    member __.IsRangeKeySpecified = notNull rangeKey
    member __.PrimaryKey = primaryKey
    member private __.Format =
        match rangeKey with
        | null -> sprintf "{ HashKey = %A }" hashKey
        | rk ->
            match hashKey with
            | null -> sprintf "{ RangeKey = %A }" rk
            | hk -> sprintf "{ HashKey = %A ; RangeKey = %A }" hk rk

    override __.ToString() = __.Format

    override __.Equals o =
        match o with
        | :? QueryKey as qk' -> hashKey = qk'.HashKey && rangeKey = qk'.RangeKey && primaryKey = qk'.PrimaryKey
        | _ -> false

    override __.GetHashCode() = hash3 hashKey rangeKey primaryKey

    /// Defines a table key using provided HashKey
    static member Hash<'HashKey>(hashKey : 'HashKey) =
        if isNull hashKey then raise <| ArgumentNullException("HashKey must not be null")
        QueryKey(hashKey, null, None)

    /// Defines a table key using combined HashKey and RangeKey
    static member Combined<'HashKey, 'RangeKey>(hashKey : 'HashKey, rangeKey : 'RangeKey) =
        if isNull hashKey then raise <| ArgumentNullException("HashKey must not be null")
        QueryKey(hashKey, rangeKey, None)

    // Defines a table key for an index query using a provided HashKey and the primary TableKey
    static member IndexQueryHash<'HashKey>(hashKey : 'HashKey, primaryKey: TableKey) =
        if isNull hashKey then raise <| ArgumentNullException("HashKey must not be null")
        QueryKey(hashKey, null, Some primaryKey)

    // Defines a table key for an index query using a combined HashKey and RangeKey, as well as the primary TableKey
    static member IndexQueryCombined<'HashKey, 'RangeKey>(hashKey : 'HashKey, rangeKey : 'RangeKey, primaryKey: TableKey) =
        if isNull hashKey then raise <| ArgumentNullException("HashKey must not be null")
        QueryKey(hashKey, rangeKey, Some primaryKey)

/// Pagination result type
type PaginatedResult<'TRecord, 'Key> =
    {
        Records : 'TRecord[]
        LastEvaluatedKey : 'Key option
    }
    interface System.Collections.IEnumerable with
        member x.GetEnumerator () = x.Records.GetEnumerator ()
    interface System.Collections.Generic.IEnumerable<'TRecord> with
        member x.GetEnumerator () = (x.Records :> System.Collections.Generic.IEnumerable<'TRecord>).GetEnumerator ()


#nowarn "1182"

/// Conditional expression special operators
[<AutoOpen>]
module ConditionalOperators =

    /// Decides whether parameter lies within given range
    let BETWEEN (x : 'T) (lower : 'T) (upper : 'T) : bool =
        lower <= x && x <= upper

    /// Checks whether a record attribute exists in DynamoDB
    let EXISTS (attr : 'T) : bool =
        invalidOp "EXISTS predicate reserved for quoted condition expressions."

    /// Checks whether a record attribute does not exist in DynamoDB
    let NOT_EXISTS (attr : 'T) : bool =
        invalidOp "NOT_EXISTS predicate reserved for quoted condition expressions."

/// Update expression special operators
[<AutoOpen>]
module UpdateOperators =

    /// Table Update operation placeholder type
    type UpdateOp =
        /// Combines two update operations into one
        static member (&&&) (left : UpdateOp, right : UpdateOp) : UpdateOp =
            invalidOp "Update combiner reserved for quoted update expressions."

    /// Assigns a record attribute path to given value
    let SET (path : 'T) (value : 'T) : UpdateOp =
        invalidOp "SET operation reserved for quoted update expressions."

    /// Removes a record attribute path from entry
    let REMOVE (path : 'T) : UpdateOp =
        invalidOp "REMOVE operation reserved for quoted update expressions."

    /// Adds given set of values to set attribute path
    let ADD (path : Set<'T>) (values : seq<'T>) : UpdateOp =
        invalidOp "ADD operation reserved for quoted update expressions."

    /// Deletes given set of values to set attribute path
    let DELETE (path : Set<'T>) (values : seq<'T>) : UpdateOp =
        invalidOp "DELETE operation reserved for quoted update expressions."
