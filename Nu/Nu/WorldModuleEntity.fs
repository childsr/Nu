﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2017.

namespace Nu
open System
open System.Collections.Generic
open OpenTK
open Prime
open Nu

[<AutoOpen; ModuleBinding>]
module WorldModuleEntity =

    /// Dynamic property getters.
    let internal Getters = Dictionary<string, Entity -> World -> Property> HashIdentity.Structural

    /// Dynamic property setters.
    let internal Setters = Dictionary<string, Property -> Entity -> World -> bool * World> HashIdentity.Structural

    /// Mutable clipboard that allows its state to persist beyond undo / redo.
    let mutable private Clipboard : obj option = None

    // avoids closure allocation in tight-loop
    type private KeyEquality () =
        inherit OptimizedClosures.FSharpFunc<
            KeyValuePair<
                Entity Address,
                UMap<Entity Address, EntityState>>,
            KeyValuePair<
                Entity Address,
                UMap<Entity Address, EntityState>>,
            bool> ()
        override this.Invoke _ = failwithumf ()
        override this.Invoke
            (entityStateKey : KeyValuePair<Entity Address, UMap<Entity Address, EntityState>>,
             entityStateKey2 : KeyValuePair<Entity Address, UMap<Entity Address, EntityState>>) =
            refEq entityStateKey.Key entityStateKey2.Key &&
            refEq entityStateKey.Value entityStateKey2.Value
    let private keyEquality = KeyEquality ()

    // avoids closure allocation in tight-loop
    let mutable private getFreshKeyAndValueEntity = Unchecked.defaultof<Entity>
    let mutable private getFreshKeyAndValueWorld = Unchecked.defaultof<World>
    let private getFreshKeyAndValue _ =
        let entityStateOpt = UMap.tryFindFast getFreshKeyAndValueEntity.EntityAddress getFreshKeyAndValueWorld.EntityStates
        KeyValuePair (KeyValuePair (getFreshKeyAndValueEntity.EntityAddress, getFreshKeyAndValueWorld.EntityStates), entityStateOpt)

    type World with

        static member private entityStateFinder (entity : Entity) world =
            // OPTIMIZATION: a ton of optimization has gone down in here...!
            let entityStateOpt = entity.EntityStateOpt
            if isNull (entityStateOpt :> obj) then
                getFreshKeyAndValueEntity <- entity
                getFreshKeyAndValueWorld <- world
                let entityStateOpt =
                    KeyedCache.getValueFast
                        keyEquality
                        getFreshKeyAndValue
                        (KeyValuePair (entity.EntityAddress, world.EntityStates))
                        (World.getEntityCachedOpt world)
                getFreshKeyAndValueEntity <- Unchecked.defaultof<Entity>
                getFreshKeyAndValueWorld <- Unchecked.defaultof<World>
                if FOption.isSome entityStateOpt then
                    let entityState = FOption.get entityStateOpt
                    if  entityState.CachableNp &&
                        Xtension.getImperative entityState.Xtension then
                        entity.EntityStateOpt <- entityState
                    entityState
                else Unchecked.defaultof<EntityState>
            else entityStateOpt

        static member private entityStateAdder entityState (entity : Entity) world =
            let screenDirectory =
                match Address.getNames entity.EntityAddress with
                | [screenName; layerName; entityName] ->
                    let layerDirectory = UMap.tryFindFast screenName world.ScreenDirectory
                    if FOption.isSome layerDirectory then
                        let layerDirectory = FOption.get layerDirectory
                        let entityDirectoryOpt = UMap.tryFindFast layerName layerDirectory.Value
                        if FOption.isSome entityDirectoryOpt then
                            let entityDirectory = FOption.get entityDirectoryOpt
                            let entityDirectory' = UMap.add entityName entity.EntityAddress entityDirectory.Value
                            let layerDirectory' = UMap.add layerName (KeyValuePair (entityDirectory.Key, entityDirectory')) layerDirectory.Value
                            UMap.add screenName (KeyValuePair (layerDirectory.Key, layerDirectory')) world.ScreenDirectory
                        else failwith ("Cannot add entity '" + scstring entity.EntityAddress + "' to non-existent layer.")
                    else failwith ("Cannot add entity '" + scstring entity.EntityAddress + "' to non-existent screen.")
                | _ -> failwith ("Invalid entity address '" + scstring entity.EntityAddress + "'.")
            let entityStates = UMap.add entity.EntityAddress entityState world.EntityStates
            World.choose { world with ScreenDirectory = screenDirectory; EntityStates = entityStates }

        static member private entityStateRemover (entity : Entity) world =
            let screenDirectory =
                match Address.getNames entity.EntityAddress with
                | [screenName; layerName; entityName] ->
                    let layerDirectoryOpt = UMap.tryFindFast screenName world.ScreenDirectory
                    if FOption.isSome layerDirectoryOpt then
                        let layerDirectory = FOption.get layerDirectoryOpt
                        let entityDirectoryOpt = UMap.tryFindFast layerName layerDirectory.Value
                        if FOption.isSome entityDirectoryOpt then
                            let entityDirectory = FOption.get entityDirectoryOpt
                            let entityDirectory' = UMap.remove entityName entityDirectory.Value
                            let layerDirectory' = UMap.add layerName (KeyValuePair (entityDirectory.Key, entityDirectory')) layerDirectory.Value
                            UMap.add screenName (KeyValuePair (layerDirectory.Key, layerDirectory')) world.ScreenDirectory
                        else failwith ("Cannot remove entity '" + scstring entity.EntityAddress + "' from non-existent layer.")
                    else failwith ("Cannot remove entity '" + scstring entity.EntityAddress + "' from non-existent screen.")
                | _ -> failwith ("Invalid entity address '" + scstring entity.EntityAddress + "'.")
            let entityStates = UMap.remove entity.EntityAddress world.EntityStates
            World.choose { world with ScreenDirectory = screenDirectory; EntityStates = entityStates }

        static member private entityStateSetter entityState (entity : Entity) world =
#if DEBUG
            if not (UMap.containsKey entity.EntityAddress world.EntityStates) then
                failwith ("Cannot set the state of a non-existent entity '" + scstring entity.EntityAddress + "'")
            if not (World.qualifyEventContext (atooa entity.EntityAddress) world) then
                failwith "Cannot set the state of an entity in an unqualifed event context."
#endif
            let entityStates = UMap.add entity.EntityAddress entityState world.EntityStates
            World.choose { world with EntityStates = entityStates }

        static member private addEntityState entityState (entity : Entity) world =
            World.entityStateAdder entityState entity world

        static member private removeEntityState (entity : Entity) world =
            World.entityStateRemover entity world

        static member private shouldPublishChange (propertyName : string) entityState  =
            if propertyName.EndsWith "Np" then false
            else entityState.PublishChanges || propertyName.EndsWith "Ap"

        static member private publishEntityChange propertyName (entity : Entity) (oldWorld : World) world =
            let changeEventAddress = ltoa ["Entity"; "Change"; propertyName; "Event"] ->>- entity.EntityAddress
            let eventTrace = EventTrace.record "World" "publishEntityChange" EventTrace.empty
            let allowWildcard = propertyName = Property? NodeOpt
            let changeData = { Participant = entity; PropertyName = propertyName; OldWorld = oldWorld }
            World.publishPlus World.sortSubscriptionsByHierarchy changeData changeEventAddress eventTrace entity allowWildcard world

        static member private getEntityStateOpt entity world =
            let entityStateOpt = World.entityStateFinder entity world
            if isNull (entityStateOpt :> obj) then None
            else Some entityStateOpt

        static member internal getEntityState entity world =
#if DEBUG
            match World.getEntityStateOpt entity world with
            | Some entityState -> entityState
            | None -> failwith ("Could not find entity with address '" + scstring entity.EntityAddress + "'.")
#else
            World.entityStateFinder entity world
#endif

        static member internal getEntityXtensionProperties entity world =
            let entityState = World.getEntityState entity world
            entityState.Xtension |> Xtension.toSeq |> Seq.toList

        static member private setEntityState entityState entity world =
            World.entityStateSetter entityState entity world

        static member private updateEntityStateInternal updater mutability entityState entity world =
            let entityState = updater entityState
            if mutability && Xtension.getImperative entityState.Xtension then (entityState, world)
            else (entityState, World.setEntityState entityState entity world)

        static member private updateEntityStateWithoutEvent updater mutability entity world =
            let entityState = World.getEntityState entity world
            let (_, world) = World.updateEntityStateInternal updater mutability entityState entity world
            world

        static member private updateEntityState updater mutability (propertyName : string) entity world =
            let oldWorld = world
            let entityState = World.getEntityState entity world
            let (entityState, world) = World.updateEntityStateInternal updater mutability entityState entity world
            if World.shouldPublishChange propertyName entityState
            then World.publishEntityChange propertyName entity oldWorld world
            else world

        static member private updateEntityStatePlus updater mutability (propertyName : string) entity world =
            let oldWorld = world
            let oldEntityState = World.getEntityState entity oldWorld
            let oldOmnipresent = oldEntityState.Omnipresent
            let oldViewType = oldEntityState.ViewType
            let oldBoundsMax = World.getEntityStateBoundsMax oldEntityState
            let (entityState, world) = World.updateEntityStateInternal updater mutability oldEntityState entity world
            let world = World.updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax entity oldWorld world
            if World.shouldPublishChange propertyName entityState
            then World.publishEntityChange propertyName entity oldWorld world
            else world

        static member private publishEntityChanges entity oldWorld world =
            let entityState = World.getEntityState entity world
            let properties = World.getProperties entityState
            if entityState.PublishChanges
            then List.fold (fun world (propertyName, _) -> World.publishEntityChange propertyName entity oldWorld world) world properties
            else world

        static member internal entityExists entity world =
            Option.isSome (World.getEntityStateOpt entity world)

        static member private getEntityStateBoundsMax entityState =
            // TODO: get up off yer arse and write an algorithm for tight-fitting bounds...
            match entityState.Rotation with
            | 0.0f ->
                let boundsOverflow = Math.makeBoundsOverflow entityState.Position entityState.Size entityState.Overflow
                boundsOverflow // no need to transform when unrotated
            | _ ->
                let boundsOverflow = Math.makeBoundsOverflow entityState.Position entityState.Size entityState.Overflow
                let position = boundsOverflow.Xy
                let size = Vector2 (boundsOverflow.Z, boundsOverflow.W) - position
                let center = position + size * 0.5f
                let corner = position + size
                let centerToCorner = corner - center
                let quaternion = Quaternion.FromAxisAngle (Vector3.UnitZ, Constants.Math.DegreesToRadiansF * 45.0f)
                let newSizeOver2 = Vector2 (Vector2.Transform (centerToCorner, quaternion)).Y
                let newPosition = center - newSizeOver2
                let newSize = newSizeOver2 * 2.0f
                Vector4 (newPosition.X, newPosition.Y, newPosition.X + newSize.X, newPosition.Y + newSize.Y)

        // NOTE: Wouldn't macros be nice?
        static member internal getEntityId entity world = (World.getEntityState entity world).Id
        static member internal getEntityName entity world = (World.getEntityState entity world).Name
        static member internal getEntityUserState entity world = (World.getEntityState entity world).UserState
        static member internal setEntityUserState value entity world = World.updateEntityState (fun entityState -> if UserState.getImperative entityState.UserState then entityState.UserState <- value; entityState else { entityState with UserState = value }) false Property? UserState entity world
        static member internal getEntityUserValue<'a> entity world = UserState.get<'a> (World.getEntityState entity world).UserState
        static member internal setEntityUserValue<'a> value entity world = World.updateEntityState (fun entityState -> if UserState.getImperative entityState.UserState then ignore (UserState.set<'a> value entityState.UserState); entityState else { entityState with UserState = UserState.set<'a> value entityState.UserState }) false Property? UserValue entity world
        static member internal getEntityDispatcherNp entity world = (World.getEntityState entity world).DispatcherNp
        static member internal getEntityPersistent entity world = (World.getEntityState entity world).Persistent
        static member internal setEntityPersistent value entity world = World.updateEntityState (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Persistent <- value; entityState else { entityState with Persistent = value }) false Property? Persistent entity world
        static member internal getEntityCreationTimeStampNp entity world = (World.getEntityState entity world).CreationTimeStampNp
        static member internal getEntityImperative entity world = Xtension.getImperative (World.getEntityState entity world).Xtension
        static member internal getEntityCachableNp entity world = (World.getEntityState entity world).CachableNp
        static member internal getEntityOverlayNameOpt entity world = (World.getEntityState entity world).OverlayNameOpt
        static member internal getEntityPosition entity world = (World.getEntityState entity world).Position
        static member internal setEntityPosition value entity world = World.updateEntityStatePlus (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Position <- value; entityState else { entityState with EntityState.Position = value }) true Property? Position entity world
        static member internal getEntitySize entity world = (World.getEntityState entity world).Size
        static member internal setEntitySize value entity world = World.updateEntityStatePlus (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Size <- value; entityState else { entityState with EntityState.Size = value }) true Property? Size entity world
        static member internal getEntityRotation entity world = (World.getEntityState entity world).Rotation
        static member internal setEntityRotation value entity world = World.updateEntityStatePlus (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Rotation <- value; entityState else { entityState with EntityState.Rotation = value }) true Property? Rotation entity world
        static member internal getEntityDepth entity world = (World.getEntityState entity world).Depth
        static member internal setEntityDepth value entity world = World.updateEntityState (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Depth <- value; entityState else { entityState with EntityState.Depth = value }) true Property? Depth entity world
        static member internal getEntityOverflow entity world = (World.getEntityState entity world).Overflow
        static member internal setEntityOverflow value entity world = World.updateEntityStatePlus (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Overflow <- value; entityState else { entityState with EntityState.Overflow = value }) true Property? Overflow entity world
        static member internal getEntityViewType entity world = (World.getEntityState entity world).ViewType
        static member internal setEntityViewType value entity world = World.updateEntityStatePlus (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.ViewType <- value; entityState else { entityState with EntityState.ViewType = value }) true Property? ViewType entity world
        static member internal getEntityVisible entity world = (World.getEntityState entity world).Visible
        static member internal setEntityVisible value entity world = World.updateEntityState (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Visible <- value; entityState else { entityState with EntityState.Visible = value }) true Property? Visible entity world
        static member internal getEntityEnabled entity world = (World.getEntityState entity world).Enabled
        static member internal setEntityEnabled value entity world = World.updateEntityState (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Enabled <- value; entityState else { entityState with EntityState.Enabled = value }) true Property? Enabled entity world
        static member internal getEntityOmnipresent entity world = (World.getEntityState entity world).Omnipresent
        static member internal setEntityOmnipresent value entity world = World.updateEntityStatePlus (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.Omnipresent <- value; entityState else { entityState with EntityState.Omnipresent = value }) true Property? Omnipresent entity world
        static member internal getEntityAlwaysUpdate entity world = (World.getEntityState entity world).AlwaysUpdate
        static member internal setEntityAlwaysUpdate value entity world = World.updateEntityStatePlus (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.AlwaysUpdate <- value; entityState else { entityState with EntityState.AlwaysUpdate = value }) true Property? AlwaysUpdate entity world
        static member internal getEntityPublishChanges entity world = (World.getEntityState entity world).PublishChanges
        static member internal setEntityPublishChanges value entity world = World.updateEntityState (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.PublishChanges <- value; entityState else { entityState with PublishChanges = value }) false Property? PublishChanges entity world
        static member internal getEntityPublishUpdatesNp entity world = (World.getEntityState entity world).PublishUpdatesNp
        static member internal setEntityPublishUpdatesNp value entity world = World.updateEntityState (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.PublishUpdatesNp <- value; entityState else { entityState with PublishUpdatesNp = value }) false Property? PublishUpdatesNp entity world
        static member internal getEntityPublishPostUpdatesNp entity world = (World.getEntityState entity world).PublishPostUpdatesNp
        static member internal setEntityPublishPostUpdatesNp value entity world = World.updateEntityState (fun entityState -> if Xtension.getImperative entityState.Xtension then entityState.PublishPostUpdatesNp <- value; entityState else { entityState with PublishPostUpdatesNp = value }) false Property? PublishPostUpdatesNp entity world
        static member internal getEntityFacetNames entity world = (World.getEntityState entity world).FacetNames
        static member internal getEntityFacetsNp entity world = (World.getEntityState entity world).FacetsNp

        static member internal getEntityTransform entity world =
            EntityState.getTransform (World.getEntityState entity world)
        
        static member internal setEntityTransform value entity world =
            let oldWorld = world
            let oldEntityState = World.getEntityState entity oldWorld
            let oldOmnipresent = oldEntityState.Omnipresent
            let oldViewType = oldEntityState.ViewType
            let oldBoundsMax = World.getEntityStateBoundsMax oldEntityState
            let world = World.updateEntityStateWithoutEvent (EntityState.setTransform value) true entity world
            let world = World.updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax entity oldWorld world
            if World.getEntityPublishChanges entity world then
                let world = World.publishEntityChange Property? Position entity oldWorld world
                let world = World.publishEntityChange Property? Size entity oldWorld world
                let world = World.publishEntityChange Property? Rotation entity oldWorld world
                World.publishEntityChange Property? Depth entity oldWorld world
            else world

        static member private tryGetFacet facetName world =
            let facets = World.getFacets world
            match Map.tryFind facetName facets with
            | Some facet -> Right facet
            | None -> Left ("Invalid facet name '" + facetName + "'.")

        static member private isFacetCompatibleWithEntity entityDispatcherMap facet (entityState : EntityState) =
            // Note a facet is incompatible with any other facet if it contains any properties that has
            // the same name but a different type.
            let facetType = facet.GetType ()
            let facetPropertyDefinitions = Reflection.getPropertyDefinitions facetType
            if Reflection.isFacetCompatibleWithDispatcher entityDispatcherMap facet entityState then
                List.notExists
                    (fun (propertyDefinition : PropertyDefinition) ->
                        match Xtension.tryGetProperty propertyDefinition.PropertyName entityState.Xtension with
                        | Some property -> property.PropertyType <> propertyDefinition.PropertyType
                        | None -> false)
                    facetPropertyDefinitions
            else false

        static member private getEntityPropertyDefinitionNamesToDetach entityState facetToRemove =

            // get the property definition name counts of the current, complete entity
            let propertyDefinitions = Reflection.getReflectivePropertyDefinitionMap entityState
            let propertyDefinitionNameCounts = Reflection.getPropertyNameCounts propertyDefinitions

            // get the property definition name counts of the facet to remove
            let facetType = facetToRemove.GetType ()
            let facetPropertyDefinitions = Map.singleton facetType.Name (Reflection.getPropertyDefinitions facetType)
            let facetPropertyDefinitionNameCounts = Reflection.getPropertyNameCounts facetPropertyDefinitions

            // compute the difference of the counts
            let finalPropertyDefinitionNameCounts =
                Map.map
                    (fun propertyName propertyCount ->
                        match Map.tryFind propertyName facetPropertyDefinitionNameCounts with
                        | Some facetPropertyCount -> propertyCount - facetPropertyCount
                        | None -> propertyCount)
                    propertyDefinitionNameCounts

            // build a set of all property names where the final counts are negative
            Map.fold
                (fun propertyNamesToDetach propertyName propertyCount ->
                    if propertyCount = 0
                    then Set.add propertyName propertyNamesToDetach
                    else propertyNamesToDetach)
                Set.empty
                finalPropertyDefinitionNameCounts

        /// Get an entity's intrinsic facet names.
        static member getEntityIntrinsicFacetNames entityState =
            let intrinsicFacetNames = entityState.DispatcherNp |> getType |> Reflection.getIntrinsicFacetNames
            Set.ofList intrinsicFacetNames

        /// Get an entity's facet names via reflection.
        static member getEntityFacetNamesReflectively entityState =
            let facetNames = List.map getTypeName entityState.FacetsNp
            Set.ofList facetNames

        static member private tryRemoveFacet facetName entityState entityOpt world =
            match List.tryFind (fun facet -> getTypeName facet = facetName) entityState.FacetsNp with
            | Some facet ->
                let (entityState, world) =
                    match entityOpt with
                    | Some entity ->
                        let world = World.setEntityState entityState entity world
                        let world = World.withEventContext (fun world -> facet.Unregister (entity, world)) entity world
                        let entityState = World.getEntityState entity world
                        (entityState, world)
                    | None -> (entityState, world)
                let propertyNames = World.getEntityPropertyDefinitionNamesToDetach entityState facet
                let entityState = Reflection.detachPropertiesViaNames EntityState.copy propertyNames entityState
                let entityState =
                    let facetNames = Set.remove facetName entityState.FacetNames
                    let facets = List.remove ((=) facet) entityState.FacetsNp
                    if Xtension.getImperative entityState.Xtension then
                        entityState.FacetNames <- facetNames
                        entityState.FacetsNp <- facets
                        entityState
                    else { entityState with FacetNames = facetNames; FacetsNp = facets }
                match entityOpt with
                | Some entity ->
                    let oldWorld = world
                    let oldEntityState = entityState
                    let oldOmnipresent = oldEntityState.Omnipresent
                    let oldViewType = oldEntityState.ViewType
                    let oldBoundsMax = World.getEntityStateBoundsMax oldEntityState
                    let world = World.setEntityState entityState entity world
                    let world = World.updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax entity oldWorld world
                    Right (World.getEntityState entity world, world)
                | None -> Right (entityState, world)
            | None -> let _ = World.choose world in Left ("Failure to remove facet '" + facetName + "' from entity.")

        static member private tryAddFacet facetName (entityState : EntityState) entityOpt world =
            match World.tryGetFacet facetName world with
            | Right facet ->
                let entityDispatchers = World.getEntityDispatchers world
                if World.isFacetCompatibleWithEntity entityDispatchers facet entityState then
                    let entityState =
                        let facetNames = Set.add facetName entityState.FacetNames
                        let facets = facet :: entityState.FacetsNp
                        if Xtension.getImperative entityState.Xtension then
                            entityState.FacetNames <- facetNames
                            entityState.FacetsNp <- facets
                            entityState
                        else { entityState with FacetNames = facetNames; FacetsNp = facets }
                    let entityState = Reflection.attachProperties EntityState.copy facet entityState
                    match entityOpt with
                    | Some entity ->
                        let oldWorld = world
                        let oldEntityState = entityState
                        let oldOmnipresent = oldEntityState.Omnipresent
                        let oldViewType = oldEntityState.ViewType
                        let oldBoundsMax = World.getEntityStateBoundsMax oldEntityState
                        let world = World.setEntityState entityState entity world
                        let world = World.updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax entity oldWorld world
                        let world = World.withEventContext (fun world -> facet.Register (entity, world)) entity world
                        Right (World.getEntityState entity world, world)
                    | None -> Right (entityState, world)
                else let _ = World.choose world in Left ("Facet '" + getTypeName facet + "' is incompatible with entity '" + scstring entityState.Name + "'.")
            | Left error -> Left error

        static member private tryRemoveFacets facetNamesToRemove entityState entityOpt world =
            Set.fold
                (fun eitherEntityWorld facetName ->
                    match eitherEntityWorld with
                    | Right (entityState, world) -> World.tryRemoveFacet facetName entityState entityOpt world
                    | Left _ as left -> left)
                (Right (entityState, world))
                facetNamesToRemove

        static member private tryAddFacets facetNamesToAdd entityState entityOpt world =
            Set.fold
                (fun eitherEntityStateWorld facetName ->
                    match eitherEntityStateWorld with
                    | Right (entityState, world) -> World.tryAddFacet facetName entityState entityOpt world
                    | Left _ as left -> left)
                (Right (entityState, world))
                facetNamesToAdd

        static member internal trySetFacetNames facetNames entityState entityOpt world =
            let intrinsicFacetNames = World.getEntityIntrinsicFacetNames entityState
            let extrinsicFacetNames = Set.fold (flip Set.remove) facetNames intrinsicFacetNames
            let facetNamesToRemove = Set.difference entityState.FacetNames extrinsicFacetNames
            let facetNamesToAdd = Set.difference extrinsicFacetNames entityState.FacetNames
            match World.tryRemoveFacets facetNamesToRemove entityState entityOpt world with
            | Right (entityState, world) -> World.tryAddFacets facetNamesToAdd entityState entityOpt world
            | Left _ as left -> left

        static member internal trySynchronizeFacetsToNames oldFacetNames entityState entityOpt world =
            let facetNamesToRemove = Set.difference oldFacetNames entityState.FacetNames
            let facetNamesToAdd = Set.difference entityState.FacetNames oldFacetNames
            match World.tryRemoveFacets facetNamesToRemove entityState entityOpt world with
            | Right (entityState, world) -> World.tryAddFacets facetNamesToAdd entityState entityOpt world
            | Left _ as left -> left

        static member internal attachIntrinsicFacetsViaNames entityState world =
            let entityDispatchers = World.getEntityDispatchers world
            let facets = World.getFacets world
            Reflection.attachIntrinsicFacets EntityState.copy entityDispatchers facets entityState.DispatcherNp entityState

        static member internal applyEntityOverlay oldOverlayer overlayer world entity =
            let entityState = World.getEntityState entity world
            match entityState.OverlayNameOpt with
            | Some overlayName ->
                let oldFacetNames = entityState.FacetNames
                let entityState = Overlayer.applyOverlayToFacetNames EntityState.copy overlayName overlayName entityState oldOverlayer overlayer
                match World.trySynchronizeFacetsToNames oldFacetNames entityState (Some entity) world with
                | Right (entityState, world) ->
                    let oldWorld = world
                    let oldEntityState = entityState
                    let oldOmnipresent = oldEntityState.Omnipresent
                    let oldViewType = oldEntityState.ViewType
                    let oldBoundsMax = World.getEntityStateBoundsMax oldEntityState
                    let facetNames = World.getEntityFacetNamesReflectively entityState
                    let entityState = Overlayer.applyOverlay6 EntityState.copy overlayName overlayName facetNames entityState oldOverlayer overlayer
                    let world = World.setEntityState entityState entity world
                    World.updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax entity oldWorld world
                | Left error -> Log.info ("There was an issue in applying a reloaded overlay: " + error); world
            | None -> world

        static member internal tryGetEntityCalculatedProperty propertyName entity world =
            let dispatcher = World.getEntityDispatcherNp entity world
            match dispatcher.TryGetCalculatedProperty (propertyName, entity, world) with
            | None ->
                List.tryFindPlus (fun (facet : Facet) ->
                    facet.TryGetCalculatedProperty (propertyName, entity, world))
                    (World.getEntityFacetsNp entity world)
            | Some _ as propertyOpt -> propertyOpt

        static member internal tryGetEntityProperty propertyName entity world =
            if World.entityExists entity world then
                match Getters.TryGetValue propertyName with
                | (false, _) ->
                    match EntityState.tryGetProperty propertyName (World.getEntityState entity world) with
                    | None -> World.tryGetEntityCalculatedProperty propertyName entity world
                    | Some _ as propertyOpt -> propertyOpt
                | (true, getter) -> Some (getter entity world)
            else None

        static member internal getEntityProperty propertyName entity world =
            match Getters.TryGetValue propertyName with
            | (false, _) ->
                match EntityState.tryGetProperty propertyName (World.getEntityState entity world) with
                | None ->
                    match World.tryGetEntityCalculatedProperty propertyName entity world with
                    | None -> failwithf "Could not find property '%s'." propertyName
                    | Some property -> property
                | Some property -> property
            | (true, getter) -> getter entity world

        static member internal trySetEntityProperty propertyName property entity world =
            if World.entityExists entity world then
                match Setters.TryGetValue propertyName with
                | (false, _) ->
                    let mutable success = false // bit of a hack to get additional state out of the lambda
                    let world =
                        World.updateEntityState (fun entityState ->
                            let (successInner, entityState) = EntityState.trySetProperty propertyName property entityState
                            success <- successInner
                            entityState)
                            true propertyName entity world
                    (success, world)
                | (true, setter) -> setter property entity world
            else (false, world)

        static member internal setEntityProperty propertyName property entity world =
            if World.entityExists entity world then
                match Setters.TryGetValue propertyName with
                | (false, _) -> World.updateEntityState (EntityState.setProperty propertyName property) true propertyName entity world
                | (true, setter) ->
                    match setter property entity world with
                    | (true, world) -> world
                    | (false, _) -> failwith ("Cannot change entity property " + propertyName + ".")
            else world

        /// Get the maxima bounds of the entity as determined by size, position, rotation, and overflow.
        static member internal getEntityBoundsMax entity world =
            let entityState = World.getEntityState entity world
            World.getEntityStateBoundsMax entityState

        /// Get the quick size of an entity (the appropriate user-defined size for an entity).
        static member internal getEntityQuickSize (entity : Entity) world =
            let dispatcher = World.getEntityDispatcherNp entity world
            let facets = World.getEntityFacetsNp entity world
            let quickSize = dispatcher.GetQuickSize (entity, world)
            List.fold
                (fun (maxSize : Vector2) (facet : Facet) ->
                    let quickSize = facet.GetQuickSize (entity, world)
                    Vector2
                        (Math.Max (quickSize.X, maxSize.X),
                         Math.Max (quickSize.Y, maxSize.Y)))
                quickSize
                facets

        /// Get an entity's sorting priority.
        static member internal getEntitySortingPriority entity world =
            let entityState = World.getEntityState entity world
            { SortDepth = entityState.Depth; SortTarget = entity }

        static member private updateEntityPublishEventFlag setFlag entity eventAddress world =
            let publishUpdates =
                let subscriptionsOpt = UMap.tryFindFast eventAddress (World.getSubscriptions world)
                if FOption.isSome subscriptionsOpt then
                    match FOption.get subscriptionsOpt with
                    | [||] -> failwithumf () // NOTE: event system is defined to clean up all empty subscription entries
                    | _ -> true
                else false
            if World.entityExists entity world
            then setFlag publishUpdates entity world
            else world

        static member internal updateEntityPublishUpdateFlag entity world =
            World.updateEntityPublishEventFlag World.setEntityPublishUpdatesNp entity (atooa entity.UpdateAddress) world

        static member internal updateEntityPublishPostUpdateFlag entity world =
            World.updateEntityPublishEventFlag World.setEntityPublishPostUpdatesNp entity (atooa entity.PostUpdateAddress) world

        static member internal updateEntityPublishFlags entity world =
            let world = World.updateEntityPublishUpdateFlag entity world
            let world = World.updateEntityPublishPostUpdateFlag entity world
            world

        static member internal registerEntity entity world =
            let world =
                World.withEventContext (fun world ->
                    let dispatcher = World.getEntityDispatcherNp entity world : EntityDispatcher
                    let facets = World.getEntityFacetsNp entity world
                    let world = dispatcher.Register (entity, world)
                    let world = List.fold (fun world (facet : Facet) -> facet.Register (entity, world)) world facets
                    let world = World.updateEntityPublishFlags entity world
                    let eventTrace = EventTrace.record "World" "registerEntity" EventTrace.empty
                    World.publish () (ltoa<unit> ["Entity"; "Register"; "Event"] ->- entity) eventTrace entity world)
                    entity
                    world
            World.choose world

        static member internal unregisterEntity entity world =
            let world =
                World.withEventContext (fun world ->
                    let eventTrace = EventTrace.record "World" "removeEntity" EventTrace.empty
                    let world = World.publish () (ltoa<unit> ["Entity"; "Unregistering"; "Event"] ->- entity) eventTrace entity world
                    let dispatcher = World.getEntityDispatcherNp entity world : EntityDispatcher
                    let facets = World.getEntityFacetsNp entity world
                    let world = dispatcher.Unregister (entity, world)
                    List.fold (fun world (facet : Facet) -> facet.Unregister (entity, world)) world facets)
                    entity
                    world
            World.choose world

        static member internal addEntity mayReplace entityState entity world =

            // add entity only if it is new or is explicitly able to be replaced
            let isNew = not (World.entityExists entity world)
            if isNew || mayReplace then

                // get old world for entity tree rebuild and change events
                let oldWorld = world
                
                // add entity to world
                let world = World.addEntityState entityState entity world

                // mutate entity tree
                let screen = entity.EntityAddress |> Address.head |> ntoa<Screen> |> Screen
                let world =
                    let entityTree =
                        MutantCache.mutateMutant
                            (fun () -> oldWorld.Dispatchers.RebuildEntityTree screen oldWorld)
                            (fun entityTree ->
                                let entityState = World.getEntityState entity world
                                let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                SpatialTree.addElement (entityState.Omnipresent || entityState.ViewType = Absolute) entityMaxBounds entity entityTree
                                entityTree)
                            (World.getScreenEntityTreeNp screen world)
                    World.setScreenEntityTreeNpNoEvent entityTree screen world

                // register entity if needed
                if isNew
                then World.registerEntity entity world
                else world

            // handle failure
            else failwith ("Adding an entity that the world already contains at address '" + scstring entity.EntityAddress + "'.")

        /// Destroy an entity in the world immediately. Can be dangerous if existing in-flight publishing depends on
        /// the entity's existence. Consider using World.destroyEntity instead.
        static member destroyEntityImmediate entity world =
            World.removeEntity entity world

        /// Create an entity and add it to the world.
        [<FunctionBinding ("createEntity")>]
        static member createEntity5 dispatcherName nameOpt overlayNameDescriptor (layer : Layer) world =

            // grab overlay dependencies
            let overlayer = World.getOverlayer world

            // find the entity's dispatcher
            let dispatchers = World.getEntityDispatchers world
            let dispatcher =
                match Map.tryFind dispatcherName dispatchers with
                | Some dispatcher -> dispatcher
                | None -> failwith ("Could not find an EntityDispatcher named '" + dispatcherName + "'. Did you forget to provide this dispatcher from your NuPlugin?")

            // compute the optional overlay name
            let overlayNameOpt =
                match overlayNameDescriptor with
                | NoOverlay -> None
                | RoutedOverlay -> Option.flatten (World.tryFindRoutedOverlayNameOpt dispatcherName world)
                | DefaultOverlay -> Some (Option.getOrDefault dispatcherName (Option.flatten (World.tryFindRoutedOverlayNameOpt dispatcherName world)))
                | ExplicitOverlay overlayName -> Some overlayName

            // make the bare entity state (with name as id if none is provided)
            let entityState = EntityState.make nameOpt overlayNameOpt dispatcher

            // attach the entity state's intrinsic facets and their properties
            let entityState = World.attachIntrinsicFacetsViaNames entityState world

            // apply the entity state's overlay to its facet names
            let entityState =
                match overlayNameOpt with
                | Some overlayName ->

                    // apply overlay to facets
                    let entityState = Overlayer.applyOverlayToFacetNames EntityState.copy dispatcherName overlayName entityState overlayer overlayer

                    // synchronize the entity's facets (and attach their properties)
                    match World.trySynchronizeFacetsToNames Set.empty entityState None world with
                    | Right (entityState, _) -> entityState
                    | Left error -> Log.debug error; entityState
                | None -> entityState

            // attach the entity state's dispatcher properties
            let entityState = Reflection.attachProperties EntityState.copy dispatcher entityState

            // apply the entity state's overlay
            let entityState =
                match entityState.OverlayNameOpt with
                | Some overlayName ->
                    // OPTIMIZATION: apply overlay only when it will change something
                    if dispatcherName <> overlayName then
                        let facetNames = World.getEntityFacetNamesReflectively entityState
                        Overlayer.applyOverlay EntityState.copy dispatcherName overlayName facetNames entityState overlayer
                    else entityState
                | None -> entityState

            // add entity's state to world
            let entity = Entity (layer.LayerAddress -<<- ntoa<Entity> entityState.Name)
            let world = World.addEntity false entityState entity world
            (entity, world)

        /// Create an entity and add it to the world.
        static member createEntity<'d when 'd :> EntityDispatcher> nameOpt overlayNameOpt layer world =
            World.createEntity5 typeof<'d>.Name nameOpt overlayNameOpt layer world

        static member private removeEntity entity world =
            
            // ensure entity exists in the world
            if World.entityExists entity world then
                
                // unregister entity
                let world = World.unregisterEntity entity world

                // get old world for entity tree rebuild
                let oldWorld = world
                
                // mutate entity tree
                let screen = entity.EntityAddress |> Address.head |> ntoa<Screen> |> Screen
                let world =
                    let entityTree =
                        MutantCache.mutateMutant
                            (fun () -> oldWorld.Dispatchers.RebuildEntityTree screen oldWorld)
                            (fun entityTree ->
                                let entityState = World.getEntityState entity oldWorld
                                let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                SpatialTree.removeElement (entityState.Omnipresent || entityState.ViewType = Absolute) entityMaxBounds entity entityTree
                                entityTree)
                            (World.getScreenEntityTreeNp screen world)
                    World.setScreenEntityTreeNpNoEvent entityTree screen world

                // remove cached entity event addresses
                EventWorld.cleanEventAddressCache entity.EntityAddress

                // remove the entity from the world
                World.removeEntityState entity world

            // pass
            else world

        /// Read an entity from an entity descriptor.
        static member readEntity entityDescriptor nameOpt (layer : Layer) world =

            // grab overlay dependencies
            let overlayer = World.getOverlayer world

            // create the dispatcher
            let dispatcherName = entityDescriptor.EntityDispatcher
            let dispatchers = World.getEntityDispatchers world
            let (dispatcherName, dispatcher) =
                match Map.tryFind dispatcherName dispatchers with
                | Some dispatcher -> (dispatcherName, dispatcher)
                | None ->
                    Log.info ("Could not locate dispatcher '" + dispatcherName + "'.")
                    let dispatcherName = typeof<EntityDispatcher>.Name
                    let dispatcher =
                        match Map.tryFind dispatcherName dispatchers with
                        | Some dispatcher -> dispatcher
                        | None -> failwith ("Could not find an EntityDispatcher named '" + dispatcherName + "'. Did you forget to provide this dispatcher from your NuPlugin?")
                    (dispatcherName, dispatcher)

            // try to route the overlay name
            let overlayNameOpt =
                match World.tryFindRoutedOverlayNameOpt dispatcherName world with
                | Some overlayNameOpt -> overlayNameOpt
                | None -> None

            // make the bare entity state with name as id
            let entityState = EntityState.make None overlayNameOpt dispatcher

            // attach the entity state's intrinsic facets and their properties
            let entityState = World.attachIntrinsicFacetsViaNames entityState world

            // read the entity state's overlay and apply it to its facet names if applicable
            let entityState = Reflection.tryReadOverlayNameOptToTarget EntityState.copy entityDescriptor.EntityProperties entityState
            let entityState =
                match (overlayNameOpt, entityState.OverlayNameOpt) with
                | (Some overlayName, Some esOverlayName) -> Overlayer.applyOverlayToFacetNames EntityState.copy overlayName esOverlayName entityState overlayer overlayer
                | (_, _) -> entityState

            // read the entity state's facet names
            let entityState = Reflection.readFacetNamesToTarget EntityState.copy entityDescriptor.EntityProperties entityState

            // attach the entity state's dispatcher properties
            let entityState = Reflection.attachProperties EntityState.copy dispatcher entityState
            
            // synchronize the entity state's facets (and attach their properties)
            let entityState =
                match World.trySynchronizeFacetsToNames Set.empty entityState None world with
                | Right (entityState, _) -> entityState
                | Left error -> Log.debug error; entityState

            // attempt to apply the entity state's overlay
            let entityState =
                match entityState.OverlayNameOpt with
                | Some overlayName ->
                    // OPTIMIZATION: applying overlay only when it will change something
                    if dispatcherName <> overlayName then
                        let facetNames = World.getEntityFacetNamesReflectively entityState
                        Overlayer.applyOverlay EntityState.copy dispatcherName overlayName facetNames entityState overlayer
                    else entityState
                | None -> entityState

            // read the entity state's values
            let entityState = Reflection.readPropertiesToTarget EntityState.copy entityDescriptor.EntityProperties entityState

            // apply the name if one is provided
            let entityState =
                match nameOpt with
                | Some name -> { entityState with Name = name }
                | None -> entityState

            // add entity state to the world
            let entity = Entity (layer.LayerAddress -<<- ntoa<Entity> entityState.Name)
            let world = World.addEntity true entityState entity world
            (entity, world)

        /// Write an entity to an entity descriptor.
        static member writeEntity (entity : Entity) entityDescriptor world =
            let entityState = World.getEntityState entity world
            let entityDispatcherName = getTypeName entityState.DispatcherNp
            let entityDescriptor = { entityDescriptor with EntityDispatcher = entityDispatcherName }
            let shouldWriteProperty = fun propertyName propertyType (propertyValue : obj) ->
                if propertyName = "OverlayNameOpt" && propertyType = typeof<string option> then
                    let defaultOverlayNameOpt = Option.flatten (World.tryFindRoutedOverlayNameOpt entityDispatcherName world)
                    defaultOverlayNameOpt <> (propertyValue :?> string option)
                else
                    let overlayer = World.getOverlayer world
                    let facetNames = World.getEntityFacetNamesReflectively entityState
                    Overlayer.shouldPropertySerialize5 facetNames propertyName propertyType entityState overlayer
            let getEntityProperties = Reflection.writePropertiesFromTarget shouldWriteProperty entityDescriptor.EntityProperties entityState
            { entityDescriptor with EntityProperties = getEntityProperties }

        /// Reassign an entity's identity and / or layer. Note that since this destroys the reassigned entity
        /// immediately, you should not call this inside an event handler that involves the reassigned entity itself.
        static member reassignEntityImmediate entity nameOpt (layer : Layer) world =
            let entityState = World.getEntityState entity world
            let world = World.removeEntity entity world
            let (id, name) = Reflection.deriveIdAndName nameOpt
            let entityState = { entityState with Id = id; Name = name }
            let transmutedEntity = Entity (layer.LayerAddress -<<- ntoa<Entity> name)
            let world = World.addEntity false entityState transmutedEntity world
            (transmutedEntity, world)

        /// Reassign an entity's identity and / or layer.
        [<FunctionBinding>]
        static member reassignEntity entity nameOpt layer world =
            let tasklet =
                { ScheduledTime = World.getTickTime world
                  Command = { Execute = fun world -> World.reassignEntityImmediate entity nameOpt layer world |> snd }}
            World.addTasklet tasklet world

        /// Try to set an entity's optional overlay name.
        static member trySetEntityOverlayNameOpt overlayNameOpt entity world =
            let oldEntityState = World.getEntityState entity world
            let oldOverlayNameOpt = oldEntityState.OverlayNameOpt
            let entityState =
                if Xtension.getImperative oldEntityState.Xtension
                then oldEntityState.OverlayNameOpt <- overlayNameOpt; oldEntityState
                else { oldEntityState with OverlayNameOpt = overlayNameOpt }
            match (oldOverlayNameOpt, overlayNameOpt) with
            | (Some oldOverlayName, Some overlayName) ->
                let overlayer = World.getOverlayer world
                let (entityState, world) =
                    let oldFacetNames = entityState.FacetNames
                    let entityState = Overlayer.applyOverlayToFacetNames EntityState.copy oldOverlayName overlayName entityState overlayer overlayer
                    match World.trySynchronizeFacetsToNames oldFacetNames entityState (Some entity) world with
                    | Right (entityState, world) -> (entityState, world)
                    | Left error -> Log.debug error; (entityState, world)
                let facetNames = World.getEntityFacetNamesReflectively entityState
                let entityState = Overlayer.applyOverlay EntityState.copy oldOverlayName overlayName facetNames entityState overlayer
                let oldWorld = world
                let oldEntityState = entityState
                let oldOmnipresent = oldEntityState.Omnipresent
                let oldViewType = oldEntityState.ViewType
                let oldBoundsMax = World.getEntityStateBoundsMax oldEntityState
                let world = World.setEntityState entityState entity world
                let world = World.updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax entity oldWorld world
                let world =
                    if World.getEntityPublishChanges entity world
                    then World.publishEntityChanges entity oldWorld world
                    else world
                (Right (), world)
            | (None, None) ->
                (Right (), world)
            | (_, _) ->
                (Left "Could not set the entity's overlay name because setting an overlay to or from None is currently unimplemented.", world)
            
        /// Try to set the entity's facet names from script.
        [<FunctionBinding ("trySetEntityOverlayNameOpt")>]
        static member trySetEntityOverlayNameOptFromScript overlayNameOpt entity world =
            match World.trySetEntityOverlayNameOpt overlayNameOpt entity world with
            | (Right _, world) -> world
            | (Left _, world) -> world

        /// Try to set the entity's facet names.
        static member trySetEntityFacetNames facetNames entity world =
            let entityState = World.getEntityState entity world
            match World.trySetFacetNames facetNames entityState (Some entity) world with
            | Right (entityState, world) ->
                let oldWorld = world
                let oldEntityState = entityState
                let oldOmnipresent = oldEntityState.Omnipresent
                let oldViewType = oldEntityState.ViewType
                let oldBoundsMax = World.getEntityStateBoundsMax oldEntityState
                let world = World.setEntityState entityState entity world
                let world = World.updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax entity oldWorld world
                let world =
                    if World.getEntityPublishChanges entity world
                    then World.publishEntityChanges entity oldWorld world
                    else world
                (Right (), world)
            | Left error -> (Left error, world)
            
        /// Try to set the entity's facet names from script.
        [<FunctionBinding ("trySetEntityFacetNames")>]
        static member trySetEntityFacetNamesFromScript facetNames entity world =
            match World.trySetEntityFacetNames facetNames entity world with
            | (Right _, world) -> world
            | (Left _, world) -> world

        /// View all of the properties of an entity.
        static member internal viewEntityProperties entity world =
            let state = World.getEntityState entity world
            let properties = World.getProperties state
            Array.ofList properties

        static member internal updateEntityInEntityTree oldOmnipresent oldViewType oldBoundsMax (entity : Entity) oldWorld world =
            
            // OPTIMIZATION: only update when entity is not omnipresent
            if  (not oldOmnipresent && oldViewType <> Absolute) ||
                (not (World.getEntityOmnipresent entity world) && World.getEntityViewType entity world <> Absolute) then

                // OPTIMIZATION: attempts to avoid constructing a screen address on each call to decrease address hashing
                // OPTIMIZATION: assumes a well-formed entity address with List.head on its names
                let screen =
                    match (World.getGameState world).SelectedScreenOpt with
                    | Some screen when Address.getName screen.ScreenAddress = List.head (Address.getNames entity.EntityAddress) -> screen
                    | Some _ | None -> entity.EntityAddress |> Address.getNames |> List.head |> ntoa<Screen> |> Screen

                // proceed to update entity in entity tree
                let entityTree =
                    MutantCache.mutateMutant
                        (fun () -> oldWorld.Dispatchers.RebuildEntityTree screen oldWorld)
                        (fun entityTree ->
                            let entityState = World.getEntityState entity world
                            let entityBoundsMax = World.getEntityStateBoundsMax entityState
                            SpatialTree.updateElement oldBoundsMax entityBoundsMax entity entityTree
                            entityTree)
                        (World.getScreenEntityTreeNp screen world)
                World.setScreenEntityTreeNpNoEvent entityTree screen world

            // just world
            else world

        /// Copy an entity to the clipboard.
        static member copyEntityToClipboard entity world =
            let entityState = World.getEntityState entity world
            Clipboard <- Some (entityState :> obj)

        /// Cut an entity to the clipboard.
        static member cutEntityToClipboard entity world =
            World.copyEntityToClipboard entity world
            World.destroyEntityImmediate entity world

        /// Paste an entity from the clipboard.
        static member pasteEntityFromClipboard atMouse rightClickPosition positionSnap rotationSnap (layer : Layer) world =
            match Clipboard with
            | Some entityStateObj ->
                let entityState = entityStateObj :?> EntityState
                let id = makeGuid ()
                let name = (scstring id)
                let entityState = { entityState with Id = id; Name = name }
                let position =
                    if atMouse
                    then World.mouseToWorld entityState.ViewType rightClickPosition world
                    else World.mouseToWorld entityState.ViewType (World.getEyeSize world * 0.5f) world
                let transform = { EntityState.getTransform entityState with Position = position }
                let transform = Math.snapTransform positionSnap rotationSnap transform
                let entityState = EntityState.setTransform transform entityState
                let entity = Entity (layer.LayerAddress -<<- ntoa<Entity> name)
                let world = World.addEntity false entityState entity world
                (Some entity, world)
            | None -> (None, world)

    /// Initialize property getters.
    let private initGetters () =
        Getters.Add ("Id", fun entity world -> { PropertyType = typeof<Guid>; PropertyValue = World.getEntityId entity world })
        Getters.Add ("Name", fun entity world -> { PropertyType = typeof<string>; PropertyValue = World.getEntityName entity world })
        Getters.Add ("UserState", fun entity world -> { PropertyType = typeof<UserState>; PropertyValue = World.getEntityUserState entity world })
        Getters.Add ("DispatcherNp", fun entity world -> { PropertyType = typeof<EntityDispatcher>; PropertyValue = World.getEntityDispatcherNp entity world })
        Getters.Add ("Persistent", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPersistent entity world })
        Getters.Add ("CreationTimeStampNp", fun entity world -> { PropertyType = typeof<int64>; PropertyValue = World.getEntityCreationTimeStampNp entity world })
        Getters.Add ("Imperative", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityImperative entity world })
        Getters.Add ("CachableNp", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityCachableNp entity world })
        Getters.Add ("OverlayNameOpt", fun entity world -> { PropertyType = typeof<string option>; PropertyValue = World.getEntityOverlayNameOpt entity world })
        Getters.Add ("Position", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntityPosition entity world })
        Getters.Add ("Size", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntitySize entity world })
        Getters.Add ("Rotation", fun entity world -> { PropertyType = typeof<single>; PropertyValue = World.getEntityRotation entity world })
        Getters.Add ("Depth", fun entity world -> { PropertyType = typeof<single>; PropertyValue = World.getEntityDepth entity world })
        Getters.Add ("Overflow", fun entity world -> { PropertyType = typeof<Vector2>; PropertyValue = World.getEntityOverflow entity world })
        Getters.Add ("ViewType", fun entity world -> { PropertyType = typeof<ViewType>; PropertyValue = World.getEntityViewType entity world })
        Getters.Add ("Visible", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityVisible entity world })
        Getters.Add ("Enabled", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityEnabled entity world })
        Getters.Add ("Omnipresent", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityOmnipresent entity world })
        Getters.Add ("AlwaysUpdate", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityAlwaysUpdate entity world })
        Getters.Add ("PublishChanges", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPublishChanges entity world })
        Getters.Add ("PublishUpdatesNp", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPublishUpdatesNp entity world })
        Getters.Add ("PublishPostUpdatesNp", fun entity world -> { PropertyType = typeof<bool>; PropertyValue = World.getEntityPublishPostUpdatesNp entity world })
        Getters.Add ("FacetNames", fun entity world -> { PropertyType = typeof<string Set>; PropertyValue = World.getEntityFacetNames entity world })
        Getters.Add ("FacetsNp", fun entity world -> { PropertyType = typeof<Facet list>; PropertyValue = World.getEntityFacetsNp entity world })

    /// Initialize property setters.
    let private initSetters () =
        Setters.Add ("Id", fun _ _ world -> (false, world))
        Setters.Add ("Name", fun _ _ world -> (false, world))
        Setters.Add ("UserState", fun property entity world -> (true, World.setEntityUserState (property.PropertyValue :?> UserState) entity world))
        Setters.Add ("DispatcherNp", fun _ _ world -> (false, world))
        Setters.Add ("Persistent", fun property entity world -> (true, World.setEntityPersistent (property.PropertyValue :?> bool) entity world))
        Setters.Add ("CreationTimeStampNp", fun _ _ world -> (false, world))
        Setters.Add ("Imperative", fun _ _ world -> (false, world))
        Setters.Add ("CachableNp", fun _ _ world -> (false, world))
        Setters.Add ("OverlayNameOpt", fun _ _ world -> (false, world))
        Setters.Add ("Position", fun property entity world -> (true, World.setEntityPosition (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("Size", fun property entity world -> (true, World.setEntitySize (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("Rotation", fun property entity world -> (true, World.setEntityRotation (property.PropertyValue :?> single) entity world))
        Setters.Add ("Depth", fun property entity world -> (true, World.setEntityDepth (property.PropertyValue :?> single) entity world))
        Setters.Add ("Overflow", fun property entity world -> (true, World.setEntityOverflow (property.PropertyValue :?> Vector2) entity world))
        Setters.Add ("ViewType", fun property entity world -> (true, World.setEntityViewType (property.PropertyValue :?> ViewType) entity world))
        Setters.Add ("Visible", fun property entity world -> (true, World.setEntityVisible (property.PropertyValue :?> bool) entity world))
        Setters.Add ("Enabled", fun property entity world -> (true, World.setEntityEnabled (property.PropertyValue :?> bool) entity world))
        Setters.Add ("Omnipresent", fun property entity world -> (true, World.setEntityOmnipresent (property.PropertyValue :?> bool) entity world))
        Setters.Add ("AlwaysUpdate", fun property entity world -> (true, World.setEntityAlwaysUpdate (property.PropertyValue :?> bool) entity world))
        Setters.Add ("PublishChanges", fun property entity world -> (true, World.setEntityPublishChanges (property.PropertyValue :?> bool) entity world))
        Setters.Add ("PublishUpdatesNp", fun _ _ world -> (false, world))
        Setters.Add ("PublishPostUpdatesNp", fun _ _ world -> (false, world))
        Setters.Add ("FacetNames", fun _ _ world -> (false, world))
        Setters.Add ("FacetsNp", fun _ _ world -> (false, world))

    /// Initialize getters and setters
    let internal init () =
        initGetters ()
        initSetters ()