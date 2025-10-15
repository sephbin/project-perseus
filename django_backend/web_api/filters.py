import django_filters
from core.models import Element

class ElementModelFilter(django_filters.FilterSet):
    # Filter for a specific date (e.g., created_at=2024-01-01)
    created_at = django_filters.DateFilter(field_name='created_at', lookup_expr='date')
    updated_at = django_filters.DateFilter(field_name='updated_at', lookup_expr='date')


    class Meta:
        model = Element
        fields = ['created_at', 'updated_at']