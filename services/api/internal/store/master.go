package store

import (
	"context"
	"encoding/json"
	"fmt"
)

// MasterData is the master-data bundle delivered to the Dedicated Server in the
// LoadBootstrap response (3.4 / 06A 0.4-2). The DS caches it in its
// MasterDataStore and treats it as the authoritative source for quantities,
// weights, recipes and tool requirements. ContentVersion lets the DS decide
// whether its cache is stale.
type MasterData struct {
	ContentVersion  int64                `json:"content_version"`
	ItemDefinitions []MasterItemDef      `json:"item_definitions"`
	Recipes         []MasterRecipe       `json:"recipes"`
	ResourceNodes   []MasterResourceNode `json:"resource_node_defs"`
}

// MasterItemDef mirrors item_definitions for delivery (7.2).
type MasterItemDef struct {
	ItemDefinitionID string          `json:"item_definition_id"`
	PrimaryTag       string          `json:"primary_tag"`
	Tags             []string        `json:"tags"`
	StackLimit       int             `json:"stack_limit"`
	WeightMilli      int64           `json:"weight_milli"`
	Rarity           int             `json:"rarity"`
	BaseValue        int64           `json:"base_value"`
	ConsumeHunger    int             `json:"consume_hunger"`
	WasteOutput      int             `json:"waste_output"`
	IsInstance       bool            `json:"is_instance"`
	UseEffect        json.RawMessage `json:"use_effect"`
}

// MasterRecipe mirrors a recipe plus its ingredients (8.4).
type MasterRecipe struct {
	RecipeID          string             `json:"recipe_id"`
	Kind              string             `json:"kind"`
	StationType       string             `json:"station_type"`
	OutputItem        string             `json:"output_item,omitempty"`
	OutputQuantity    int                `json:"output_quantity"`
	CraftSeconds      int                `json:"craft_seconds"`
	UnlockBlueprint   string             `json:"unlock_blueprint,omitempty"`
	RequiredBlueprint string             `json:"required_blueprint,omitempty"`
	Ingredients       []MasterIngredient `json:"ingredients"`
}

// MasterIngredient is one recipe input (8.4).
type MasterIngredient struct {
	ItemDefinitionID string `json:"item_definition_id"`
	Quantity         int    `json:"quantity"`
}

// MasterResourceNode mirrors resource_node_defs (8.3).
type MasterResourceNode struct {
	ResourceType       string          `json:"resource_type"`
	DropItem           string          `json:"drop_item"`
	RequiredToolTags   []string        `json:"required_tool_tags"`
	Hardness           int             `json:"hardness"`
	MaximumAmount      int             `json:"maximum_amount"`
	Quality            int             `json:"quality"`
	RegenerationPolicy json.RawMessage `json:"regeneration_policy"`
}

// LoadMasterData reads all master tables and assembles the bundle for delivery.
// content_version comes from worlds.content_version (used as the master version
// the DS keys its cache on). A missing world yields content_version 0.
func (s *Store) LoadMasterData(ctx context.Context, worldID string) (*MasterData, error) {
	md := &MasterData{}

	// content_version (master version tag for the DS cache).
	if err := s.pool.QueryRow(ctx,
		`SELECT coalesce((SELECT content_version FROM worlds WHERE world_id = $1), 0)`, worldID,
	).Scan(&md.ContentVersion); err != nil {
		return nil, fmt.Errorf("store: master content_version: %w", err)
	}

	items, err := s.loadMasterItems(ctx)
	if err != nil {
		return nil, err
	}
	md.ItemDefinitions = items

	recipes, err := s.loadMasterRecipes(ctx)
	if err != nil {
		return nil, err
	}
	md.Recipes = recipes

	nodes, err := s.loadMasterNodes(ctx)
	if err != nil {
		return nil, err
	}
	md.ResourceNodes = nodes
	return md, nil
}

func (s *Store) loadMasterItems(ctx context.Context) ([]MasterItemDef, error) {
	rows, err := s.pool.Query(ctx,
		`SELECT item_definition_id, primary_tag, tags, stack_limit, weight_milli, rarity,
		        base_value, consume_hunger, waste_output, is_instance, use_effect
		   FROM item_definitions ORDER BY item_definition_id`)
	if err != nil {
		return nil, fmt.Errorf("store: load master items: %w", err)
	}
	defer rows.Close()

	var out []MasterItemDef
	for rows.Next() {
		var d MasterItemDef
		var useEffect []byte
		if err := rows.Scan(&d.ItemDefinitionID, &d.PrimaryTag, &d.Tags, &d.StackLimit, &d.WeightMilli,
			&d.Rarity, &d.BaseValue, &d.ConsumeHunger, &d.WasteOutput, &d.IsInstance, &useEffect); err != nil {
			return nil, fmt.Errorf("store: scan master item: %w", err)
		}
		d.UseEffect = json.RawMessage(useEffect)
		out = append(out, d)
	}
	return out, rows.Err()
}

func (s *Store) loadMasterRecipes(ctx context.Context) ([]MasterRecipe, error) {
	rows, err := s.pool.Query(ctx,
		`SELECT recipe_id, kind, station_type, coalesce(output_item, ''), output_quantity,
		        craft_seconds, coalesce(unlock_blueprint, ''), coalesce(required_blueprint, '')
		   FROM recipes ORDER BY recipe_id`)
	if err != nil {
		return nil, fmt.Errorf("store: load master recipes: %w", err)
	}
	defer rows.Close()

	byID := map[string]*MasterRecipe{}
	var order []string
	for rows.Next() {
		var r MasterRecipe
		if err := rows.Scan(&r.RecipeID, &r.Kind, &r.StationType, &r.OutputItem, &r.OutputQuantity,
			&r.CraftSeconds, &r.UnlockBlueprint, &r.RequiredBlueprint); err != nil {
			return nil, fmt.Errorf("store: scan master recipe: %w", err)
		}
		r.Ingredients = []MasterIngredient{}
		byID[r.RecipeID] = &r
		order = append(order, r.RecipeID)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	irows, err := s.pool.Query(ctx,
		`SELECT recipe_id, item_definition_id, quantity FROM recipe_ingredients
		  ORDER BY recipe_id, item_definition_id`)
	if err != nil {
		return nil, fmt.Errorf("store: load recipe ingredients: %w", err)
	}
	defer irows.Close()
	for irows.Next() {
		var recipeID string
		var ing MasterIngredient
		if err := irows.Scan(&recipeID, &ing.ItemDefinitionID, &ing.Quantity); err != nil {
			return nil, fmt.Errorf("store: scan ingredient: %w", err)
		}
		if r, ok := byID[recipeID]; ok {
			r.Ingredients = append(r.Ingredients, ing)
		}
	}
	if err := irows.Err(); err != nil {
		return nil, err
	}

	out := make([]MasterRecipe, 0, len(order))
	for _, id := range order {
		out = append(out, *byID[id])
	}
	return out, nil
}

func (s *Store) loadMasterNodes(ctx context.Context) ([]MasterResourceNode, error) {
	rows, err := s.pool.Query(ctx,
		`SELECT resource_type, drop_item, required_tool_tags, hardness, maximum_amount,
		        quality, regeneration_policy
		   FROM resource_node_defs ORDER BY resource_type`)
	if err != nil {
		return nil, fmt.Errorf("store: load master nodes: %w", err)
	}
	defer rows.Close()

	var out []MasterResourceNode
	for rows.Next() {
		var n MasterResourceNode
		var policy []byte
		if err := rows.Scan(&n.ResourceType, &n.DropItem, &n.RequiredToolTags, &n.Hardness,
			&n.MaximumAmount, &n.Quality, &policy); err != nil {
			return nil, fmt.Errorf("store: scan master node: %w", err)
		}
		n.RegenerationPolicy = json.RawMessage(policy)
		out = append(out, n)
	}
	return out, rows.Err()
}
